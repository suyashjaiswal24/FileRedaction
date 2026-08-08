using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileRedaction.Models;

namespace FileRedaction.Services;

public interface IPiiDetectionService
{
    Task<List<PiiEntityResult>> DetectPiiAsync(string fullText, List<WordInfo> words, List<PageInfo> pages, string language = "en");
}

/// <summary>
/// Calls the Azure AI Language Service PII detection REST API directly.
/// https://learn.microsoft.com/azure/ai-services/language-service/personally-identifiable-information/how-to/redact-text-pii
/// </summary>
public class PiiDetectionService : IPiiDetectionService
{
    // Language Service hard limits: 5 documents per request, 5120 chars per document.
    private const int MaxDocsPerBatch = 5;
    private const int MaxCharsPerDoc = 5120;

    // Transient HTTP status codes worth retrying
    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes =
        [HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout, HttpStatusCode.RequestTimeout];

    private readonly HttpClient _httpClient;
    // Pre-built absolute URL — avoids .NET Uri combining which percent-encodes the
    // literal ':' in ':analyze-text', turning it into '%3Aanalyze-text' and causing a 404.
    private readonly string _analyzeTextUrl;
    private readonly int _maxRetries;
    private readonly double _retryBaseDelaySecs;
    private readonly double _retryMaxDelaySecs;
    private readonly ILogger<PiiDetectionService> _logger;

    public PiiDetectionService(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<PiiDetectionService> logger)
    {
        _httpClient = httpClientFactory.CreateClient(nameof(PiiDetectionService));

        var rawEndpoint = config["AzureLanguageService:Endpoint"]
            ?? throw new InvalidOperationException("AzureLanguageService:Endpoint is not configured.");
        var cleanEndpoint = Uri.UnescapeDataString(rawEndpoint).Trim('"', '\'', ' ').TrimEnd('/');
        // Build the full URL as a plain string — no Uri class involved, no encoding of ':'
        _analyzeTextUrl = $"{cleanEndpoint}/language/:analyze-text?api-version=2024-11-01";

        _maxRetries = config.GetValue("AzureLanguageService:MaxRetries", 3);
        _retryBaseDelaySecs = config.GetValue("AzureLanguageService:RetryBaseDelaySeconds", 0.8);
        _retryMaxDelaySecs = config.GetValue("AzureLanguageService:RetryMaxDelaySeconds", 10.0);
        _logger = logger;
    }

    public async Task<List<PiiEntityResult>> DetectPiiAsync(string fullText, List<WordInfo> words, List<PageInfo> pages, string language = "en")
    {
        var chunks = SplitByPage(fullText, pages);

        _logger.LogInformation(
            "═══ PII DETECTION START ═══  Total chars: {Chars}  Page-chunks: {Chunks}  Language: {Lang}  URL: {Url}",
            fullText.Length, chunks.Count, language, _analyzeTextUrl);

        for (int i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            _logger.LogInformation(
                "  Chunk {Idx}/{Total}  page={Page}  sub={Sub}  offset={Offset}  len={Len}  preview: {Preview}",
                i + 1, chunks.Count, c.PageNumber, c.SubChunk,
                c.Offset, c.Text.Length,
                c.Text.Length > 200 ? c.Text[..200] + "…" : c.Text);
        }

        var allEntities = new List<PiiEntityResult>();

        // 5 documents per request — Language Service hard limit
        for (int batchStart = 0; batchStart < chunks.Count; batchStart += MaxDocsPerBatch)
        {
            var batch = chunks.Skip(batchStart).Take(MaxDocsPerBatch).ToList();

            // Doc IDs are batch-relative (0–4) so they always fit the response index
            var docs = batch.Select((c, i) => new LsDocument
            {
                Id = i.ToString(),
                Language = language,
                Text = c.Text
            }).ToList();

            _logger.LogInformation(
                "  → Batch {BatchNum}: {Count} doc(s), pages {Pages}",
                batchStart / MaxDocsPerBatch + 1,
                docs.Count,
                string.Join(",", batch.Select(c => c.SubChunk > 0 ? $"{c.PageNumber}.{c.SubChunk}" : $"{c.PageNumber}")));

            var response = await PostWithRetryAsync(docs);

            _logger.LogInformation(
                "  ← Batch response: {DocCount} result(s), {ErrCount} error(s), model={Model}",
                response.Results.Documents.Count,
                response.Results.Errors.Count,
                response.Results.ModelVersion);

            foreach (var doc in response.Results.Documents)
            {
                int batchDocIdx = int.Parse(doc.Id);
                var chunk = batch[batchDocIdx];

                _logger.LogInformation(
                    "    Doc[page={Page}{Sub}]: {Count} entity/entities",
                    chunk.PageNumber,
                    chunk.SubChunk > 0 ? $".{chunk.SubChunk}" : "",
                    doc.Entities.Count);

                foreach (var entity in doc.Entities)
                {
                    // entity.Offset is relative to the chunk; add chunk.Offset to get absolute position in fullText
                    int absoluteOffset = chunk.Offset + entity.Offset;

                    _logger.LogInformation(
                        "      [{Cat}] \"{Text}\"  score={Score:P0}  absOffset={Offset}  len={Len}",
                        entity.Category, entity.Text, entity.ConfidenceScore,
                        absoluteOffset, entity.Length);

                    var matchingWords = words.Where(w =>
                        w.Offset < absoluteOffset + entity.Length &&
                        w.Offset + w.Length > absoluteOffset).ToList();

                    if (matchingWords.Count == 0)
                    {
                        _logger.LogWarning(
                            "      ⚠ No DI words matched for '{Text}' at offset {Offset} — still redactable via TextFragmentAbsorber",
                            entity.Text, absoluteOffset);
                    }

                    allEntities.Add(new PiiEntityResult
                    {
                        Id = Guid.NewGuid().ToString("N")[..8],
                        Text = entity.Text,
                        Category = entity.Category,
                        SubCategory = entity.SubCategory ?? string.Empty,
                        ConfidenceScore = entity.ConfidenceScore,
                        BoundingRegions = matchingWords.Select(w => new BoundingRegion
                        {
                            PageNumber = w.PageNumber,
                            Polygon = w.BoundingPolygon,
                            IsPixelUnit = w.IsPixelUnit
                        }).ToList()
                    });
                }
            }

            foreach (var err in response.Results.Errors)
                _logger.LogWarning("  ⚠ Language Service error doc {Id}: {Error}", err.Id, err.Error?.Message);
        }

        // Merge: same text+category can appear across multiple page-chunks.
        // Collect ALL bounding regions so every occurrence gets redacted.
        var deduplicated = allEntities
            .GroupBy(e => $"{e.Text.ToLowerInvariant()}|{e.Category}")
            .Select(g =>
            {
                var best = g.OrderByDescending(e => e.ConfidenceScore).First();
                best.BoundingRegions = g.SelectMany(e => e.BoundingRegions).ToList();
                best.OccurrenceCount = g.Count();
                return best;
            })
            .ToList();

        _logger.LogInformation(
            "═══ PII DETECTION DONE ═══  {Count} unique entities:\n{List}",
            deduplicated.Count,
            string.Join("\n", deduplicated.Select(e =>
                $"    [{e.Category}] \"{e.Text}\"  score={e.ConfidenceScore:P0}  regions={e.BoundingRegions.Count}")));

        return deduplicated;
    }

    private async Task<LsAnalyzeResponse> PostWithRetryAsync(List<LsDocument> documents)
    {
        var requestBody = new LsAnalyzeRequest
        {
            Kind = "PiiEntityRecognition",
            AnalysisInput = new LsAnalysisInput { Documents = documents },
            Parameters = new LsParameters { ModelVersion = "latest" }
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions.Default);
        _logger.LogInformation("  REQUEST BODY → {Json}", json);

        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            if (attempt > 0)
            {
                // Exponential backoff: base * 2^(attempt-1), capped at max
                double delaySecs = Math.Min(_retryBaseDelaySecs * Math.Pow(2, attempt - 1), _retryMaxDelaySecs);
                _logger.LogWarning("Retrying Language Service call (attempt {Attempt}/{Max}), waiting {Delay:F1}s",
                    attempt, _maxRetries, delaySecs);
                await Task.Delay(TimeSpan.FromSeconds(delaySecs));
            }

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            HttpResponseMessage httpResponse;
            try
            {
                httpResponse = await _httpClient.PostAsync(_analyzeTextUrl, content);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException || ex.CancellationToken == default)
            {
                if (attempt == _maxRetries) throw new InvalidOperationException("Language Service timed out after all retries.", ex);
                _logger.LogWarning("Language Service request timed out (attempt {Attempt})", attempt + 1);
                continue;
            }

            if (httpResponse.IsSuccessStatusCode)
            {
                var responseJson = await httpResponse.Content.ReadAsStringAsync();
                _logger.LogInformation("  RESPONSE BODY ← {Json}", responseJson);
                return JsonSerializer.Deserialize<LsAnalyzeResponse>(responseJson, JsonOptions.Default)
                       ?? throw new InvalidOperationException("Empty response from Language Service.");
            }

            if (RetryableStatusCodes.Contains(httpResponse.StatusCode) && attempt < _maxRetries)
            {
                // Honour Retry-After header if present (429 Too Many Requests)
                if (httpResponse.Headers.RetryAfter?.Delta is { } retryAfter)
                    await Task.Delay(retryAfter);
                continue;
            }

            var body = await httpResponse.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Language Service returned {(int)httpResponse.StatusCode}: {body}");
        }

        throw new InvalidOperationException("Language Service failed after all retries.");
    }

    private readonly record struct PageChunk(string Text, int Offset, int PageNumber, int SubChunk);

    /// <summary>
    /// Splits the document into one Language Service document per page using
    /// <see cref="PageInfo.TextOffset"/>/<see cref="PageInfo.TextLength"/> from the DI result —
    /// the authoritative page spans, not inferred from word positions.
    /// This ensures every page is sent even if it has no word tokens (e.g. image-only pages).
    /// Pages whose text exceeds 5120 chars are sub-chunked at newline boundaries.
    /// </summary>
    private static List<PageChunk> SplitByPage(string fullText, List<PageInfo> pages)
    {
        var result = new List<PageChunk>();

        foreach (var page in pages.OrderBy(p => p.PageNumber))
        {
            if (page.TextLength <= 0) continue; // blank / image-only page — nothing to send

            // Guard against DI returning spans that exceed the actual text length
            int safeLength = Math.Min(page.TextLength, fullText.Length - page.TextOffset);
            if (safeLength <= 0) continue;

            string pageText = fullText.Substring(page.TextOffset, safeLength);

            if (pageText.Length <= MaxCharsPerDoc)
            {
                result.Add(new PageChunk(pageText, page.TextOffset, page.PageNumber, 0));
            }
            else
            {
                // Sub-chunk at newline boundaries so sentences aren't split mid-token
                int subStart = 0, subIdx = 0;
                while (subStart < pageText.Length)
                {
                    int len = Math.Min(MaxCharsPerDoc, pageText.Length - subStart);
                    if (len < pageText.Length - subStart)
                    {
                        int boundary = pageText.LastIndexOf('\n', subStart + len, len);
                        if (boundary > subStart) len = boundary - subStart + 1;
                    }
                    result.Add(new PageChunk(
                        pageText.Substring(subStart, len),
                        page.TextOffset + subStart,
                        page.PageNumber,
                        subIdx));
                    subStart += len;
                    subIdx++;
                }
            }
        }

        return result;
    }

    // ── Language Service JSON contract ─────────────────────────────────────

    private record LsAnalyzeRequest
    {
        [JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;
        [JsonPropertyName("analysisInput")] public LsAnalysisInput AnalysisInput { get; init; } = new();
        [JsonPropertyName("parameters")] public LsParameters Parameters { get; init; } = new();
    }

    private record LsAnalysisInput
    {
        [JsonPropertyName("documents")] public List<LsDocument> Documents { get; init; } = new();
    }

    private record LsParameters
    {
        [JsonPropertyName("modelVersion")] public string ModelVersion { get; init; } = "latest";
        [JsonPropertyName("loggingOptOut")] public bool LoggingOptOut { get; init; } = false;
    }

    private record LsDocument
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("language")] public string Language { get; init; } = "en";
        [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
    }


    private record LsAnalyzeResponse
    {
        [JsonPropertyName("results")] public LsResults Results { get; init; } = new();
    }

    private record LsResults
    {
        [JsonPropertyName("documents")] public List<LsDocumentResult> Documents { get; init; } = new();
        [JsonPropertyName("errors")] public List<LsDocumentError> Errors { get; init; } = new();
        [JsonPropertyName("modelVersion")] public string ModelVersion { get; init; } = string.Empty;
    }

    private record LsDocumentResult
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("entities")] public List<LsEntity> Entities { get; init; } = new();
        [JsonPropertyName("redactedText")] public string RedactedText { get; init; } = string.Empty;
    }

    private record LsDocumentError
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("error")] public LsError? Error { get; init; }
    }

    private record LsError
    {
        [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    }

    private record LsEntity
    {
        [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
        [JsonPropertyName("category")] public string Category { get; init; } = string.Empty;
        [JsonPropertyName("subcategory")] public string? SubCategory { get; init; }
        [JsonPropertyName("offset")] public int Offset { get; init; }
        [JsonPropertyName("length")] public int Length { get; init; }
        [JsonPropertyName("confidenceScore")] public double ConfidenceScore { get; init; }
    }

    private static class JsonOptions
    {
        public static readonly JsonSerializerOptions Default = new()
        {
            PropertyNameCaseInsensitive = true
        };
    }
}
