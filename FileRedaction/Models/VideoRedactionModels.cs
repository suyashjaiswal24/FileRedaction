namespace FileRedaction.Models;

public class VideoSessionData
{
    public string SessionId { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string Status { get; set; } = "processing";
    // Sub-status shown to user: uploading_to_sr | detecting | redacting | publishing | ready | error
    public string Phase { get; set; } = "uploading_to_sr";
    public string? ErrorMessage { get; set; }
    public string? DownloadUrl { get; set; }
    public string? MediaId { get; set; }
}

public class VideoStatusResponse
{
    public string Status { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string? DownloadUrl { get; set; }
    public string? OriginalFileName { get; set; }
}
