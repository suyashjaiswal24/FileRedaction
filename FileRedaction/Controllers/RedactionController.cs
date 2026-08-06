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
        ".docx", ".doc", ".docm", ".odt", ".rtf",
        ".xlsx", ".xls", ".ods",
        ".pptx", ".ppt", ".odp",
        ".png", ".jpg", ".jpeg", ".tiff", ".tif", ".bmp", ".gif", ".webp"
    };

    private static readonly HashSet<string> ImageExtensions =
    [".png", ".jpg", ".jpeg", ".tiff", ".tif", ".bmp", ".gif", ".webp"];

    private static readonly FileExtensionContentTypeProvider _mimeProvider = new();

    private static string GetMimeType(string filePath)
    {
        if (_mimeProvider.TryGetContentType(filePath, out var mime)) return mime;
        return "application/octet-stream";
    }

    private readonly IDocumentIntelligenceService _docIntelligence;
    private readonly IPiiDetectionService _piiDetection;
    private readonly IRedactionService _redaction;
    private readonly IOfficeConversionService _officeConverter;
    private readonly SessionStore _sessions;
    private readonly IConfiguration _config;
    private readonly ILogger<RedactionController> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public RedactionController(
        IDocumentIntelligenceService docIntelligence,
        IPiiDetectionService piiDetection,
        IRedactionService redaction,
        IOfficeConversionService officeConverter,
        SessionStore sessions,
        IConfiguration config,
        ILogger<RedactionController> logger,
        IServiceScopeFactory scopeFactory)
    {
        _docIntelligence = docIntelligence;
        _piiDetection = piiDetection;
        _redaction = redaction;
        _officeConverter = officeConverter;
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
        _ = Task.Run(() => ProcessUploadAsync(sessionId, filePath, _scopeFactory));

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

    private async Task ProcessUploadAsync(string sessionId, string filePath, IServiceScopeFactory scopeFactory)
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
            // DI extraction runs on the ORIGINAL file — Azure DI natively supports XLSX/DOCX/PPTX,
            // so a 76KB Excel stays 76KB here instead of becoming a 6MB Aspose-converted PDF.
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
    /// Converts Office file to PDF on first preview/redact request and caches the path.
    /// Subsequent calls reuse the cached PDF.
    /// </summary>
    private string GetOrConvertToPdf(SessionData session)
    {
        if (session.PdfFilePath is not null) return session.PdfFilePath;

        lock (session)
        {
            if (session.PdfFilePath is not null) return session.PdfFilePath;

            _logger.LogInformation("Lazy PDF conversion triggered for session {Id}", session.SessionId);
            session.PdfFilePath = _officeConverter.ConvertToPdf(session.FilePath);
            return session.PdfFilePath;
        }
    }

    [HttpPost("preview")]
    public async Task<ActionResult> Preview([FromBody] PreviewRequest request)
    {
        var session = _sessions.Get(request.SessionId);
        if (session is null) return NotFound("Session not found.");

        if (!request.SelectedEntityIds.Any())
            return BadRequest("No entities selected.");

        // Resolve which file to use: Office formats convert to PDF on first call
        var ext = Path.GetExtension(session.FilePath).ToLowerInvariant();
        string workingFilePath;
        string workingExt;
        if (_officeConverter.NeedsPdfConversion(ext))
        {
            workingFilePath = GetOrConvertToPdf(session);
            workingExt = ".pdf";
        }
        else
        {
            workingFilePath = session.FilePath;
            workingExt = ext;
        }

        var selectedSet = new HashSet<string>(request.SelectedEntityIds);
        var cachedSet = new HashSet<string>(session.CachedHighlightEntityIds);
        var removedIds = cachedSet.Except(selectedSet).ToHashSet();
        var addedIds = selectedSet.Except(cachedSet).ToHashSet();

        bool hasCachedFile = session.CachedHighlightWorkingPath != null
                             && System.IO.File.Exists(session.CachedHighlightWorkingPath);
        bool canIncremental = hasCachedFile && !removedIds.Any() && addedIds.Any();

        string previewFilePath;
        bool hasHighlights;

        if (workingExt == ".pdf" || ImageExtensions.Contains(workingExt))
        {
            string highlighted;
            if (canIncremental)
            {
                var added = session.Entities.Where(e => addedIds.Contains(e.Id)).ToList();
                _logger.LogInformation("Incremental highlight: adding {Count} entity/entities to cached preview", added.Count);
                highlighted = await _redaction.AddHighlightsToExistingAsync(session.CachedHighlightWorkingPath!, added);
            }
            else
            {
                var selected = session.Entities.Where(e => selectedSet.Contains(e.Id)).ToList();
                highlighted = await _redaction.CreateHighlightedPreviewAsync(workingFilePath, selected);
            }
            session.CachedHighlightWorkingPath = highlighted;
            session.CachedHighlightEntityIds = selectedSet.ToList();
            previewFilePath = highlighted;
            hasHighlights = true;
        }
        else if (_officeConverter.IsExcelFormat(workingExt))
        {
            string highlightedXlsx;
            if (canIncremental)
            {
                var addedTexts = session.Entities.Where(e => addedIds.Contains(e.Id)).Select(e => e.Text).Distinct().ToList();
                _logger.LogInformation("Incremental Excel highlight: adding {Count} text(s) to cached xlsx", addedTexts.Count);
                highlightedXlsx = _officeConverter.AddHighlightsToExistingExcel(session.CachedHighlightWorkingPath!, addedTexts);
            }
            else
            {
                var allTexts = session.Entities.Where(e => selectedSet.Contains(e.Id)).Select(e => e.Text).Distinct().ToList();
                highlightedXlsx = _officeConverter.CreateHighlightedExcel(workingFilePath, allTexts);
            }
            session.CachedHighlightWorkingPath = highlightedXlsx;
            session.CachedHighlightEntityIds = selectedSet.ToList();
            previewFilePath = _officeConverter.ExportExcelToHtml(highlightedXlsx);
            hasHighlights = true;
        }
        else
        {
            previewFilePath = workingFilePath;
            hasHighlights = false;
        }

        var fileType = Path.GetExtension(previewFilePath).TrimStart('.');
        string fileUrl;
        if (fileType == "html")
        {
            // Serve via directory-aware endpoint so companion files (CSS, sheet HTMs) resolve correctly
            var dir = Path.GetDirectoryName(previewFilePath)!;
            var dirToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(dir));
            fileUrl = $"/api/redaction/preview-html/{Uri.EscapeDataString(dirToken)}/preview.html";
        }
        else
        {
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(previewFilePath));
            fileUrl = $"/api/redaction/file/{Uri.EscapeDataString(token)}";
        }

        return Ok(new
        {
            fileUrl,
            hasHighlights,
            fileType
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
    /// Serves Aspose-generated HTML preview directories so companion files (CSS, sheet HTMs) resolve correctly.
    /// The browser fetches preview.html first, then requests relative paths like preview_files/sheet001.htm
    /// which all resolve to this same endpoint.
    /// </summary>
    [HttpGet("preview-html/{dirToken}/{*relativePath}")]
    public IActionResult GetPreviewHtmlFile(string dirToken, string relativePath)
    {
        string dir;
        try
        {
            dir = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(dirToken)));
        }
        catch
        {
            return BadRequest("Invalid token.");
        }

        var safeDir = Path.GetFullPath(dir);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!safeDir.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            return Forbid();

        var safePath = Path.GetFullPath(Path.Combine(safeDir, relativePath));
        // Prevent path traversal outside the preview directory
        if (!safePath.StartsWith(safeDir, StringComparison.OrdinalIgnoreCase))
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

        var selected = session.Entities
            .Where(e => request.SelectedEntityIds.Contains(e.Id))
            .ToList();

        var ext = Path.GetExtension(session.FilePath).ToLowerInvariant();
        string redactedPath;

        if (_officeConverter.IsExcelFormat(ext))
        {
            // Excel: in-place cell text replacement — output stays as Excel
            var texts = selected.Select(e => e.Text).Distinct().ToList();
            redactedPath = _officeConverter.RedactExcel(session.FilePath, texts);
        }
        else
        {
            // PDF / images / Word / Slides (converted to PDF): use redaction service
            var redactFilePath = _officeConverter.NeedsPdfConversion(ext)
                ? GetOrConvertToPdf(session)
                : session.FilePath;
            redactedPath = await _redaction.ApplyPermanentRedactionAsync(redactFilePath, selected);
        }

        var redactedExt = Path.GetExtension(redactedPath).ToLowerInvariant();
        var mime = GetMimeType(redactedPath);
        var downloadName = Path.GetFileNameWithoutExtension(session.OriginalFileName) + "_redacted" + redactedExt;
        return PhysicalFile(redactedPath, mime, downloadName);
    }
}
