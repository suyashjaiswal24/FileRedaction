namespace FileRedaction.Models;

public class AudioTranscriptWord
{
    public string Word { get; set; } = string.Empty;
    public int TextOffset { get; set; }   // char position in FullTranscript
    public int TextLength { get; set; }
    public long AudioOffsetTicks { get; set; }   // 100-ns ticks
    public long AudioDurationTicks { get; set; }
}

public class AudioTimeRange
{
    public long StartTicks { get; set; }
    public long EndTicks { get; set; }
}

public class AudioPiiEntity
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public double ConfidenceScore { get; set; }
    public List<AudioTimeRange> TimeRanges { get; set; } = new();
}

public class AudioSessionData
{
    public string SessionId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;        // decoded 16kHz WAV temp file
    public string OriginalFileName { get; set; } = string.Empty;
    public string FullTranscript { get; set; } = string.Empty;
    public List<AudioPiiEntity> Entities { get; set; } = new();
    public string Status { get; set; } = "processing";          // processing | ready | error
    public string? ErrorMessage { get; set; }
    public string DetectedLanguage { get; set; } = "en";
    // Kept after processing to support manual word addition (FindTimeRanges lookup)
    public List<AudioTranscriptWord> TranscriptWords { get; set; } = new();
}

public class AudioAddEntityRequest
{
    public string Text { get; set; } = string.Empty;
}

public class AudioStatusResponse
{
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string? Transcript { get; set; }
    public List<AudioPiiEntity>? Entities { get; set; }
    public string? OriginalFileName { get; set; }
}

public class AudioRedactRequest
{
    public string SessionId { get; set; } = string.Empty;
    public List<string> SelectedEntityIds { get; set; } = new();
}
