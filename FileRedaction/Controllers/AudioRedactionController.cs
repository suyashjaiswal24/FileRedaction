using FileRedaction.Models;
using FileRedaction.Services;
using Microsoft.AspNetCore.Mvc;

namespace FileRedaction.Controllers;

[ApiController]
[Route("api/audio")]
public class AudioRedactionController : ControllerBase
{
    private static readonly HashSet<string> AllowedExts =
        [".wav", ".mp3", ".m4a", ".aac", ".ogg", ".flac", ".wma"];

    private readonly AudioSessionStore _sessions;
    private readonly IAudioTranscriptionService _transcription;
    private readonly IPiiDetectionService _pii;
    private readonly IAudioRedactionService _redaction;
    private readonly ILogger<AudioRedactionController> _logger;

    public AudioRedactionController(
        AudioSessionStore sessions,
        IAudioTranscriptionService transcription,
        IPiiDetectionService pii,
        IAudioRedactionService redaction,
        ILogger<AudioRedactionController> logger)
    {
        _sessions = sessions;
        _transcription = transcription;
        _pii = pii;
        _redaction = redaction;
        _logger = logger;
    }

    [HttpPost("upload")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100 MB
    public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] string language = "en-US")
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExts.Contains(ext))
            return BadRequest($"Unsupported audio format '{ext}'. Supported: {string.Join(", ", AllowedExts)}");

        // Save to temp
        var tempDir = Path.Combine(Path.GetTempPath(), "fileredaction_audio");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, $"{Guid.NewGuid():N}{ext}");
        await using (var fs = System.IO.File.OpenWrite(filePath))
            await file.CopyToAsync(fs);

        var session = _sessions.Create(filePath, file.FileName);
        _logger.LogInformation("Audio upload accepted: session={Id} file={File} lang={Lang}", session.SessionId, file.FileName, language);

        // INTEGRATION NOTE: Replace Task.Run below with:
        //   1. Upload filePath to Azure Blob Storage → get FileRef GUID
        //   2. Publish TranscriptionVoice to VoiceTranscriptionTopic via ServiceBusSender
        //      (language maps to SourceLanguage in TranscriptionVoicePayload)
        //      (see INTEGRATION NOTE at the top of AudioTranscriptionService.cs for the full payload shape)
        //   3. Remove ProcessAsync entirely — result arrives via POST /api/audio/transcription-result
        _ = Task.Run(() => ProcessAsync(session.SessionId, filePath, language));

        return Ok(new { sessionId = session.SessionId, originalFileName = file.FileName, status = "processing" });
    }

    [HttpGet("{sessionId}/status")]
    public IActionResult GetStatus(string sessionId)
    {
        var session = _sessions.Get(sessionId);
        if (session == null) return NotFound();

        return Ok(new AudioStatusResponse
        {
            Status = session.Status,
            ErrorMessage = session.ErrorMessage,
            Transcript = session.Status == "ready" ? session.FullTranscript : null,
            Entities = session.Status == "ready" ? session.Entities : null,
            OriginalFileName = session.OriginalFileName
        });
    }

    [HttpPost("redact")]
    public IActionResult Redact([FromBody] AudioRedactRequest request)
    {
        var session = _sessions.Get(request.SessionId);
        if (session == null) return NotFound();
        if (session.Status != "ready") return BadRequest("Session not ready.");

        var selectedSet = new HashSet<string>(request.SelectedEntityIds);
        var timeRanges = session.Entities
            .Where(e => selectedSet.Contains(e.Id))
            .SelectMany(e => e.TimeRanges)
            .ToList();

        if (timeRanges.Count == 0)
            return BadRequest("No PII time ranges found for selected entities.");

        var redactedPath = _redaction.CreateRedactedAudio(session.FilePath, timeRanges);
        var fileName = Path.GetFileNameWithoutExtension(session.OriginalFileName) + "_redacted.wav";

        return PhysicalFile(redactedPath, "audio/wav", fileName);
    }

    [HttpGet("{sessionId}/search-words")]
    public IActionResult SearchWords(string sessionId, [FromQuery] string q)
    {
        var session = _sessions.Get(sessionId);
        if (session == null) return NotFound();
        if (session.Status != "ready" || string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Ok(Array.Empty<object>());

        var matches = session.TranscriptWords
            .Where(w => w.Word.Contains(q, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(w => w.Word.ToLowerInvariant())
            .Take(10)
            .Select(w => new { text = w.Word, offsetSeconds = w.AudioOffsetTicks / 10_000_000.0 })
            .ToList();

        return Ok(matches);
    }

    [HttpPost("{sessionId}/add-entity")]
    public IActionResult AddEntity(string sessionId, [FromBody] AudioAddEntityRequest request)
    {
        var session = _sessions.Get(sessionId);
        if (session == null) return NotFound();
        if (session.Status != "ready") return BadRequest("Session not ready.");

        var text = request.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return BadRequest("Text is required.");

        var timeRanges = FindTimeRanges(text, session.FullTranscript, session.TranscriptWords);

        var entity = new AudioPiiEntity
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Text = text,
            Category = "Manual",
            ConfidenceScore = 1.0,
            TimeRanges = timeRanges
        };

        session.Entities.Add(entity);
        _logger.LogInformation("Manual entity added: '{Text}' — {Count} time range(s)", text, timeRanges.Count);
        return Ok(entity);
    }

    // ── Background processing ────────────────────────────────────────────────
    // INTEGRATION NOTE: In production this entire ProcessAsync method is replaced by a
    // Service Bus callback endpoint. The flow becomes:
    //
    //   [HttpPost("transcription-result")]                    ← new endpoint
    //   public IActionResult TranscriptionResult([FromBody] UpdateVoiceTranscriptionPayload payload)
    //   {
    //       // payload.TranscriptionId maps back to the sessionId stored at upload time
    //       var session = _sessions.GetByTranscriptionId(payload.TranscriptionId);
    //       if (!payload.IsSuccess) { session.Status = "error"; ... return Ok(); }
    //
    //       // Decrypt payload.TranscribedText using payload.Key/CryptoDetails
    //       string fullTranscript = Decrypt(payload.TranscribedText);
    //       var transcriptWords   = payload.WordTimestamps;   ← NEW field (see below)
    //       ... (rest of PII detection + entity matching is identical)
    //   }
    //
    // Required change to UpdateVoiceTranscriptionPayload (WhistleB.IVR.ServiceBus.Model):
    //   Add: public List<WordTimestamp> WordTimestamps { get; set; } = new();
    //        public record WordTimestamp(string Word, long OffsetTicks, long DurationTicks);
    //
    // Required change to TranscriptionSpeechVoice Azure Function:
    //   After calling SpeechRecognizer with RequestWordLevelTimestamps() + OutputFormat.Detailed,
    //   parse NBest[0].Words from the detailed JSON result and populate WordTimestamps
    //   before publishing UpdateVoiceTranscription back to the topic.
    //   (The Speech SDK already returns this data — it just isn't forwarded currently.)

    private async Task ProcessAsync(string sessionId, string filePath, string language)
    {
        var session = _sessions.Get(sessionId)!;
        try
        {
            var (decodedWav, transcriptWords, detectedLang, fullTranscript) =
                await _transcription.TranscribeAsync(filePath, language);

            // Update session with decoded WAV path (used for redaction)
            session.FilePath = decodedWav;

            var pages = new List<PageInfo>
            {
                new() { PageNumber = 1, TextOffset = 0, TextLength = fullTranscript.Length }
            };
            // Pass empty words list — we do our own time-range matching below
            var piiEntities = await _pii.DetectPiiAsync(fullTranscript, new List<WordInfo>(), pages, detectedLang);

            // Match each PII entity's char range against transcribed words to get time ranges
            var audioPiiEntities = piiEntities.Select(entity =>
            {
                var timeRanges = FindTimeRanges(entity.Text, fullTranscript, transcriptWords);
                return new AudioPiiEntity
                {
                    Id = entity.Id,
                    Text = entity.Text,
                    Category = entity.Category,
                    ConfidenceScore = entity.ConfidenceScore,
                    TimeRanges = timeRanges
                };
            }).ToList();

            session.FullTranscript = fullTranscript;
            session.TranscriptWords = transcriptWords;
            session.Entities = audioPiiEntities;
            session.DetectedLanguage = detectedLang;
            session.Status = "ready";

            _logger.LogInformation("Audio session {Id} ready: {Entities} entities", sessionId, audioPiiEntities.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio processing failed for session {Id}", sessionId);
            session.Status = "error";
            session.ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// Finds all occurrences of entityText in fullTranscript and maps them to audio time ranges
    /// by overlapping character positions with transcriptWords.
    /// </summary>
    private static List<AudioTimeRange> FindTimeRanges(
        string entityText, string fullTranscript, List<AudioTranscriptWord> words)
    {
        var ranges = new List<AudioTimeRange>();
        int searchFrom = 0;

        while (true)
        {
            int pos = fullTranscript.IndexOf(entityText, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (pos < 0) break;

            int end = pos + entityText.Length;
            if (IsWordBoundary(fullTranscript, pos, entityText.Length))
            {
                var matching = words.Where(w => w.TextOffset < end && w.TextOffset + w.TextLength > pos).ToList();
                if (matching.Count > 0)
                {
                    ranges.Add(new AudioTimeRange
                    {
                        StartTicks = matching.Min(w => w.AudioOffsetTicks),
                        EndTicks   = matching.Max(w => w.AudioOffsetTicks + w.AudioDurationTicks)
                    });
                }
            }
            searchFrom = pos + 1;
        }

        return ranges;
    }

    private static bool IsWordBoundary(string text, int pos, int len)
    {
        bool before = pos == 0 || !char.IsLetterOrDigit(text[pos - 1]);
        bool after  = pos + len >= text.Length || !char.IsLetterOrDigit(text[pos + len]);
        return before && after;
    }
}
