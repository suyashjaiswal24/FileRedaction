using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;

namespace FileRedaction.Services;

public interface IDocumentIntelligenceService
{
    Task<DocumentExtractionResult> AnalyzeDocumentAsync(string filePath);
}

public class DocumentExtractionResult
{
    public string FullText { get; set; } = string.Empty;
    public List<WordInfo> Words { get; set; } = new();
    /// <summary>One entry per page, ordered by page number. Spans reference positions in FullText.</summary>
    public List<PageInfo> Pages { get; set; } = new();
}

public class PageInfo
{
    public int PageNumber { get; set; }
    /// <summary>Start offset of this page's text inside FullText.</summary>
    public int TextOffset { get; set; }
    /// <summary>Length of this page's text inside FullText.</summary>
    public int TextLength { get; set; }
}

public class WordInfo
{
    public string Content { get; set; } = string.Empty;
    public int Offset { get; set; }
    public int Length { get; set; }
    public int PageNumber { get; set; }
    // [x1,y1, x2,y2, x3,y3, x4,y4] — pixels for images, inches for PDFs
    public double[] BoundingPolygon { get; set; } = Array.Empty<double>();
    // True when DI page.Unit == Pixel (image files)
    public bool IsPixelUnit { get; set; }
}

public class DocumentIntelligenceService : IDocumentIntelligenceService
{
    private readonly DocumentAnalysisClient _client;
    private readonly ILogger<DocumentIntelligenceService> _logger;

    public DocumentIntelligenceService(IConfiguration config, ILogger<DocumentIntelligenceService> logger)
    {
        var endpoint = config["Azure:DocumentIntelligence:Endpoint"]
            ?? throw new InvalidOperationException("DocumentIntelligence Endpoint not configured");
        var apiKey = config["Azure:DocumentIntelligence:ApiKey"]
            ?? throw new InvalidOperationException("DocumentIntelligence ApiKey not configured");

        _client = new DocumentAnalysisClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
        _logger = logger;
    }

    public async Task<DocumentExtractionResult> AnalyzeDocumentAsync(string filePath)
    {
        _logger.LogInformation("═══ DOCUMENT INTELLIGENCE ═══ Sending '{File}' for analysis", Path.GetFileName(filePath));

        await using var stream = File.OpenRead(filePath);
        var operation = await _client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-read", stream);
        var result = operation.Value;

        var words = new List<WordInfo>();
        var pages = new List<PageInfo>();

        foreach (var page in result.Pages)
        {
            // page.Spans gives the authoritative character range for this page in result.Content.
            // A page may have zero words (image-only, blank, etc.) but still has a span.
            foreach (var span in page.Spans)
            {
                pages.Add(new PageInfo
                {
                    PageNumber = page.PageNumber,
                    TextOffset = span.Index,
                    TextLength = span.Length
                });
            }

            bool isPixel = page.Unit == DocumentPageLengthUnit.Pixel;
            foreach (var word in page.Words)
            {
                words.Add(new WordInfo
                {
                    Content = word.Content,
                    Offset = word.Span.Index,
                    Length = word.Span.Length,
                    PageNumber = page.PageNumber,
                    IsPixelUnit = isPixel,
                    BoundingPolygon = word.BoundingPolygon
                        .SelectMany(p => new[] { (double)p.X, (double)p.Y })
                        .ToArray()
                });
            }
        }

        var fullText = result.Content;

        // ── Console summary ────────────────────────────────────────────────
        _logger.LogInformation(
            "═══ EXTRACTED TEXT SUMMARY ═══\n" +
            "  Pages reported by DI : {PageCount}\n" +
            "  Page spans collected : {SpanCount}\n" +
            "  Words                : {WordCount}\n" +
            "  Total chars          : {CharCount}\n" +
            "  Per-page breakdown   :\n{PageBreakdown}\n" +
            "  Text preview         : {Preview}",
            result.Pages.Count,
            pages.Count,
            words.Count,
            fullText.Length,
            string.Join("\n", pages.Select(p =>
                $"    Page {p.PageNumber}: offset={p.TextOffset} len={p.TextLength}  " +
                $"preview: {(p.TextLength > 0 ? fullText.Substring(p.TextOffset, Math.Min(80, p.TextLength)).Replace('\n', '↵') + (p.TextLength > 80 ? "…" : "") : "(empty)")}")),
            fullText.Length > 300 ? fullText[..300] + " …(truncated)" : fullText);

        // ── Dump full text to a temp file for easy inspection ──────────────
        var dumpPath = Path.Combine(Path.GetTempPath(), "fileredaction",
            $"extracted_text_{Path.GetFileNameWithoutExtension(filePath)}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dumpPath)!);
        await File.WriteAllTextAsync(dumpPath, fullText);
        _logger.LogInformation("═══ FULL EXTRACTED TEXT saved to: {DumpPath}", dumpPath);

        return new DocumentExtractionResult { FullText = fullText, Words = words, Pages = pages };
    }
}
