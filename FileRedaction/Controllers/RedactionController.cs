using FileRedaction.Models;
using FileRedaction.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace FileRedaction.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RedactionController : ControllerBase
{
    private static readonly string[] AllowedExtensions =
    {
        ".pdf",
        ".docx", ".doc",
        ".xlsx", ".xls",
        ".pptx", ".ppt",
        ".png", ".jpg", ".jpeg", ".tiff", ".tif", ".bmp", ".gif", ".webp"
    };

    private static readonly HashSet<string> ImageExtensions =
    [".png", ".jpg", ".jpeg", ".tiff", ".tif", ".bmp", ".gif", ".webp"];

    private static readonly HashSet<string> OfficeExtensions =
    [".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt"];

    private static readonly FileExtensionContentTypeProvider _mimeProvider = new();

    private static string GetMimeType(string filePath)
    {
        if (_mimeProvider.TryGetContentType(filePath, out var mime)) return mime;
        return "application/octet-stream";
    }

    private readonly IDocumentIntelligenceService _docIntelligence;
    private readonly IPiiDetectionService _piiDetection;
    private readonly IRedactionService _redaction;
    private readonly SessionStore _sessions;
    private readonly IConfiguration _config;
    private readonly ILogger<RedactionController> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public RedactionController(
        IDocumentIntelligenceService docIntelligence,
        IPiiDetectionService piiDetection,
        IRedactionService redaction,
        SessionStore sessions,
        IConfiguration config,
        ILogger<RedactionController> logger,
        IServiceScopeFactory scopeFactory)
    {
        _docIntelligence = docIntelligence;
        _piiDetection = piiDetection;
        _redaction = redaction;
        _sessions = sessions;
        _config = config;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Phase 1 (sync): saves the file and returns a sessionId immediately.
    /// Phase 2 (background): DI extraction + PII detection run async; poll GET /{sessionId}/status.
    /// </summary>
    [HttpPost("upload")]
    [RequestSizeLimit(20_971_520)] // 20 MB
    public async Task<ActionResult<UploadAcceptedResponse>> Upload(IFormFile file)
    {
        if (file is null || file.Length == 0)
            return BadRequest("No file provided.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            return BadRequest($"Unsupported file type '{ext}'. Supported: {string.Join(", ", AllowedExtensions)}");

        var sessionId = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "fileredaction", sessionId);
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, file.FileName);

        await using (var fs = System.IO.File.Create(filePath))
            await file.CopyToAsync(fs);

        _logger.LogInformation("Uploaded {Name} ({Size:N0} bytes), session {Id}", file.FileName, file.Length, sessionId);

        _sessions.Set(sessionId, new SessionData
        {
            SessionId = sessionId,
            FilePath = filePath,
            OriginalFileName = file.FileName,
            Status = "processing",
            Phase = "extracting"
        });

        // Phase 2: fire and forget — in the real project this becomes an Azure Function trigger
        _ = Task.Run(() => ProcessUploadAsync(sessionId, filePath, file.FileName, _scopeFactory));

        return Ok(new UploadAcceptedResponse
        {
            SessionId = sessionId,
            OriginalFileName = file.FileName,
            Status = "processing"
        });
    }

    /// <summary>Poll this after upload to check if background processing has finished.</summary>
    [HttpGet("{sessionId}/status")]
    public ActionResult<UploadStatusResponse> GetUploadStatus(string sessionId)
    {
        var session = _sessions.Get(sessionId);
        if (session is null) return NotFound("Session not found.");

        return Ok(new UploadStatusResponse
        {
            Status = session.Status,
            Phase = session.Phase,
            ErrorMessage = session.ErrorMessage,
            Entities = session.Status == "ready" ? session.Entities : null,
            OriginalFileName = session.Status == "ready" ? session.OriginalFileName : null
        });
    }

    private async Task ProcessUploadAsync(string sessionId, string filePath, string originalFileName, IServiceScopeFactory scopeFactory)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var docIntelligence = scope.ServiceProvider.GetRequiredService<IDocumentIntelligenceService>();
        var piiDetection = scope.ServiceProvider.GetRequiredService<IPiiDetectionService>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionStore>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RedactionController>>();

        var session = sessions.Get(sessionId);
        if (session is null) return;

        try
        {
            session.Phase = "extracting";
            logger.LogInformation("Background: starting DI extraction for session {Id}", sessionId);
            var extraction = await docIntelligence.AnalyzeDocumentAsync(filePath);

            session.Phase = "detecting";
            logger.LogInformation("Background: starting PII detection for session {Id}", sessionId);
            var entities = await piiDetection.DetectPiiAsync(extraction.FullText, extraction.Words, extraction.Pages);

            var wordList = extraction.Words
                .GroupBy(w => w.Content.ToLowerInvariant())
                .Select(g => g.First())
                .Select(w => new WordSearchResult
                {
                    Text = w.Content,
                    PageNumber = w.PageNumber,
                    Polygon = w.BoundingPolygon,
                    IsPixelUnit = w.IsPixelUnit
                })
                .ToList();

            session.Entities = entities;
            session.Words = wordList;
            session.Phase = string.Empty;
            session.Status = "ready";

            logger.LogInformation("Background: processing complete for session {Id} — {Count} entities", sessionId, entities.Count);
        }
        catch (Exception ex)
        {
            session.Status = "error";
            session.Phase = string.Empty;
            session.ErrorMessage = ex.Message;
            logger.LogError(ex, "Background: processing failed for session {Id}", sessionId);
        }
    }

    /// <summary>
    /// Generates a preview URL for docpreview.stackkitlabs.com.
    /// For PDFs: creates a highlighted copy (yellow annotations) and serves that.
    /// For all other formats: serves the original file — docpreview handles rendering.
    /// </summary>
    [HttpPost("preview")]
    public async Task<ActionResult> Preview([FromBody] PreviewRequest request)
    {
        var session = _sessions.Get(request.SessionId);
        if (session is null) return NotFound("Session not found.");

        if (!request.SelectedEntityIds.Any())
            return BadRequest("No entities selected.");

        var ext = Path.GetExtension(session.FilePath).ToLowerInvariant();
        var canHighlight = ext == ".pdf" || ImageExtensions.Contains(ext);

        string previewFilePath;
        if (canHighlight)
        {
            var selected = session.Entities
                .Where(e => request.SelectedEntityIds.Contains(e.Id))
                .ToList();
            previewFilePath = await _redaction.CreateHighlightedPreviewAsync(session.FilePath, selected);
        }
        else
        {
            // Office formats: serve original — DocPreview renders it; highlights not supported without Aspose.Words/Cells/Slides
            previewFilePath = session.FilePath;
        }

        var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(previewFilePath));
        var fileUrl = $"/api/redaction/file/{Uri.EscapeDataString(token)}";

        return Ok(new
        {
            fileUrl,
            hasHighlights = canHighlight,
            fileType = ext.TrimStart('.')
        });
    }

    /// <summary>Serves a temp file by its base64-encoded path token (restricted to the system temp directory).</summary>
    [HttpGet("file/{token}")]
    public IActionResult GetFile(string token)
    {
        string path;
        try
        {
            path = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(token)));
        }
        catch
        {
            return BadRequest("Invalid token.");
        }

        // Security: restrict to temp directory
        var safePath = Path.GetFullPath(path);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!safePath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        if (!System.IO.File.Exists(safePath))
            return NotFound();

        return PhysicalFile(safePath, GetMimeType(safePath));
    }

    /// <summary>
    /// Search the DI-extracted word list for manual entity addition.
    /// Returns distinct matching words with their page and polygon data.
    /// </summary>
    [HttpGet("{sessionId}/search-words")]
    public ActionResult<IEnumerable<WordSearchResult>> SearchWords(string sessionId, [FromQuery] string q)
    {
        var session = _sessions.Get(sessionId);
        if (session is null) return NotFound("Session not found.");
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return Ok(Array.Empty<WordSearchResult>());

        var term = q.Trim();
        var matches = session.Words
            .Where(w => w.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
            .GroupBy(w => w.Text.ToLowerInvariant())
            .Select(g => g.First())
            .OrderBy(w => w.Text)
            .Take(30)
            .ToList();

        return Ok(matches);
    }

    /// <summary>
    /// Manually adds a word or phrase to a session's entity list.
    /// For image files: resolves bounding regions immediately from DI word list.
    /// For PDFs: TextFragmentAbsorber resolves positions at redaction time.
    /// </summary>
    [HttpPost("{sessionId}/add-entity")]
    public ActionResult<PiiEntityResult> AddEntity(string sessionId, [FromBody] AddEntityRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest("Text must not be empty.");

        var session = _sessions.Get(sessionId);
        if (session is null) return NotFound("Session not found.");

        // Don't add an exact duplicate
        if (session.Entities.Any(e =>
                string.Equals(e.Text, request.Text.Trim(), StringComparison.OrdinalIgnoreCase) &&
                e.Category == "Manual"))
        {
            return Conflict("This word/phrase has already been added manually.");
        }

        // For image files: resolve bounding regions now using the stored DI word list
        var ext = Path.GetExtension(session.FilePath).ToLowerInvariant();
        List<BoundingRegion> regions = ImageExtensions.Contains(ext)
            ? session.Words
                .Where(w => w.Text.Contains(request.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(w => new BoundingRegion
                {
                    PageNumber = w.PageNumber,
                    Polygon = w.Polygon,
                    IsPixelUnit = w.IsPixelUnit
                })
                .ToList()
            : new List<BoundingRegion>(); // PDF: TextFragmentAbsorber resolves at redaction time

        var entity = new PiiEntityResult
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Text = request.Text.Trim(),
            Category = "Manual",
            SubCategory = string.Empty,
            ConfidenceScore = 1.0,
            BoundingRegions = regions
        };

        session.Entities.Add(entity);
        _logger.LogInformation("Manual entity added: \"{Text}\" (session {Id}), {RegionCount} region(s)", entity.Text, sessionId, regions.Count);

        return Ok(entity);
    }

    /// <summary>Permanently redacts selected entities and streams the redacted PDF for download.</summary>
    [HttpPost("redact")]
    public async Task<IActionResult> Redact([FromBody] RedactRequest request)
    {
        var session = _sessions.Get(request.SessionId);
        if (session is null) return NotFound("Session not found.");

        if (!request.SelectedEntityIds.Any())
            return BadRequest("No entities selected.");

        var ext = Path.GetExtension(session.FilePath).ToLowerInvariant();
        if (OfficeExtensions.Contains(ext))
            return BadRequest($"Permanent redaction of {ext.TrimStart('.')} files is not yet supported. Please use a PDF or image file.");

        var selected = session.Entities
            .Where(e => request.SelectedEntityIds.Contains(e.Id))
            .ToList();

        var redactedPath = await _redaction.ApplyPermanentRedactionAsync(session.FilePath, selected);

        var mime = GetMimeType(redactedPath);
        var downloadName = Path.GetFileNameWithoutExtension(session.OriginalFileName) + "_redacted" + ext;
        return PhysicalFile(redactedPath, mime, downloadName);
    }
}
