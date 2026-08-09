using FileRedaction.Models;
using FileRedaction.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileRedaction.Controllers;

[ApiController]
[Route("api/video")]
public class VideoRedactionController : ControllerBase
{
    private static readonly HashSet<string> AllowedExts =
        [".mp4", ".mov", ".avi", ".mkv", ".webm", ".wmv"];

    private readonly VideoSessionStore _sessions;
    private readonly ISecureRedactService _secureRedact;
    private readonly ILogger<VideoRedactionController> _logger;

    public VideoRedactionController(
        VideoSessionStore sessions,
        ISecureRedactService secureRedact,
        ILogger<VideoRedactionController> logger)
    {
        _sessions = sessions;
        _secureRedact = secureRedact;
        _logger = logger;
    }

    [HttpPost("upload")]
    [RequestSizeLimit(500 * 1024 * 1024)] // 500 MB for video
    public async Task<IActionResult> Upload([FromForm] IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExts.Contains(ext))
            return BadRequest($"Unsupported video format '{ext}'. Supported: {string.Join(", ", AllowedExts)}");

        var tempDir = Path.Combine(Path.GetTempPath(), "fileredaction_video");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, $"{Guid.NewGuid():N}{ext}");
        await using (var fs = System.IO.File.OpenWrite(filePath))
            await file.CopyToAsync(fs);

        var session = _sessions.Create(file.FileName);
        _logger.LogInformation("Video upload accepted: session={Id} file={File}", session.SessionId, file.FileName);

        _ = Task.Run(() => ProcessAsync(session.SessionId, filePath, file.FileName));

        return Ok(new { sessionId = session.SessionId, originalFileName = file.FileName, status = "processing" });
    }

    [HttpGet("{sessionId}/status")]
    public IActionResult GetStatus(string sessionId)
    {
        var session = _sessions.Get(sessionId);
        if (session == null) return NotFound();

        return Ok(new VideoStatusResponse
        {
            Status = session.Status,
            Phase = session.Phase,
            ErrorMessage = session.ErrorMessage,
            DownloadUrl = session.Status == "ready" ? session.DownloadUrl : null,
            OriginalFileName = session.OriginalFileName
        });
    }

    // ── Background processing ────────────────────────────────────────────────

    private async Task ProcessAsync(string sessionId, string filePath, string fileName)
    {
        var session = _sessions.Get(sessionId)!;
        try
        {
            // 1. Upload to SecureRedact
            session.Phase = "uploading_to_sr";
            var mediaId = await _secureRedact.UploadMediaAsync(filePath, fileName);
            session.MediaId = mediaId;
            _logger.LogInformation("Video uploaded to SecureRedact: mediaId={Id}", mediaId);

            // 2. Poll until 'detected'
            session.Phase = "detecting";
            await PollUntilAsync(mediaId, "detected", session, maxWaitMins: 30);

            // 3. Trigger redaction
            session.Phase = "redacting";
            var versionId = await _secureRedact.RedactMediaAsync(mediaId);
            _logger.LogInformation("Redaction triggered: versionId={Ver}", versionId);

            // 4. Poll until 'redacted'
            await PollUntilAsync(mediaId, "redacted", session, maxWaitMins: 60);

            // 5. Publish → get download URL
            session.Phase = "publishing";
            var downloadUrl = await _secureRedact.PublishMediaAsync(mediaId, versionId);
            _logger.LogInformation("Video published: url={Url}", downloadUrl);

            session.DownloadUrl = downloadUrl;
            session.Status = "ready";
            session.Phase = "ready";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video processing failed for session {Id}", sessionId);
            session.Status = "error";
            session.ErrorMessage = ex.Message;
        }
        finally
        {
            // Clean up temp file
            try { System.IO.File.Delete(filePath); } catch { /* best effort */ }
        }
    }

    private async Task PollUntilAsync(string mediaId, string targetStatus, VideoSessionData session, int maxWaitMins)
    {
        var deadline = DateTime.UtcNow.AddMinutes(maxWaitMins);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var status = await _secureRedact.GetMediaStatusAsync(mediaId);
            _logger.LogInformation("SecureRedact status for {Id}: {Status}", mediaId, status);

            if (status == targetStatus) return;

            if (status is "error" or "failed")
                throw new InvalidOperationException($"SecureRedact processing failed with status '{status}'.");
        }
        throw new InvalidOperationException($"Timed out waiting for SecureRedact status '{targetStatus}' after {maxWaitMins} minutes.");
    }
}
