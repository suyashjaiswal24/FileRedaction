extern alias AsposeDrawing;
using AD = AsposeDrawing::System.Drawing;
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
    /// <summary>ISO 639-1 code of the dominant language detected by DI (e.g. "de", "fr"). Defaults to "en".</summary>
    public string DetectedLanguage { get; set; } = "en";
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

    // Azure DI F0 free tier image limit — large images are compressed before submission
    private const long MaxImageBytes = 4_000_000;

    private static readonly HashSet<string> ImageExts =
        [".png", ".jpg", ".jpeg", ".bmp", ".tiff", ".tif", ".gif", ".webp"];

    public async Task<DocumentExtractionResult> AnalyzeDocumentAsync(string filePath)
    {
        var fileSize = new FileInfo(filePath).Length;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        _logger.LogInformation("═══ DOCUMENT INTELLIGENCE ═══ Sending '{File}' ({Size:N0} bytes) for analysis",
            Path.GetFileName(filePath), fileSize);

        Stream stream;
        if (ImageExts.Contains(ext) && fileSize > MaxImageBytes)
        {
            _logger.LogInformation("Image exceeds {Max:N0} bytes — compressing before DI submission", MaxImageBytes);
            stream = CompressImage(filePath);
            _logger.LogInformation("Compressed to {Size:N0} bytes", stream.Length);
        }
        else
        {
            stream = File.OpenRead(filePath);
        }

        await using (stream)
        {
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

        // Pick the highest-confidence language detected by DI across all spans
        var detectedLanguage = result.Languages
            .OrderByDescending(l => l.Confidence)
            .Select(l => l.Locale)
            .FirstOrDefault() ?? "en";

        // DI returns full locale codes like "en-US" or "de-DE" — Language Service PII wants just the base "en"/"de"
        if (detectedLanguage.Contains('-'))
            detectedLanguage = detectedLanguage.Split('-')[0];

        _logger.LogInformation("DI detected language: {Lang}", detectedLanguage);

        return new DocumentExtractionResult { FullText = fullText, Words = words, Pages = pages, DetectedLanguage = detectedLanguage };
        } // end await using stream
    }

    private static MemoryStream CompressImage(string filePath)
    {
        using var src = new AD.Bitmap(new MemoryStream(File.ReadAllBytes(filePath)));

        // Scale down proportionally so the compressed JPEG fits under MaxDiBytes
        var fileSize = new FileInfo(filePath).Length;
        float scale = Math.Min(1f, (float)Math.Sqrt((double)MaxImageBytes / fileSize) * 0.9f);
        int w = Math.Max(1, (int)(src.Width * scale));
        int h = Math.Max(1, (int)(src.Height * scale));

        using var resized = new AD.Bitmap(w, h);
        using (var g = AD.Graphics.FromImage(resized))
            g.DrawImage(src, 0, 0, w, h);

        var ms = new MemoryStream();
        var jpegEncoder = AsposeDrawing::System.Drawing.Imaging.ImageCodecInfo
            .GetImageEncoders()
            .First(c => c.FormatID == AsposeDrawing::System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        var encoderParams = new AsposeDrawing::System.Drawing.Imaging.EncoderParameters(1);
        encoderParams.Param[0] = new AsposeDrawing::System.Drawing.Imaging.EncoderParameter(
            AsposeDrawing::System.Drawing.Imaging.Encoder.Quality, 75L);
        resized.Save(ms, jpegEncoder, encoderParams);
        ms.Position = 0;
        return ms;
    }
}
