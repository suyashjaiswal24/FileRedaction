namespace FileRedaction.Models;

/// <summary>Returned immediately from POST /upload — processing continues in background.</summary>
public class UploadAcceptedResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string Status { get; set; } = "processing";
}

/// <summary>Returned by GET /{sessionId}/status while polling.</summary>
public class UploadStatusResponse
{
    public string Status { get; set; } = string.Empty;   // processing | ready | error
    public string Phase { get; set; } = string.Empty;    // extracting | detecting | ""
    public string? ErrorMessage { get; set; }
    public List<PiiEntityResult>? Entities { get; set; }
    public string? OriginalFileName { get; set; }
}

/// <summary>Legacy shape kept for internal use (EntitySelector, preview, etc.).</summary>
public class UploadResponse
{
    public string SessionId { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public List<PiiEntityResult> Entities { get; set; } = new();
}

public class PiiEntityResult
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string SubCategory { get; set; } = string.Empty;
    public double ConfidenceScore { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public List<BoundingRegion> BoundingRegions { get; set; } = new();
}

public class BoundingRegion
{
    public int PageNumber { get; set; }
    // [x1,y1, x2,y2, x3,y3, x4,y4] — pixels for image files, inches for PDFs
    public double[] Polygon { get; set; } = Array.Empty<double>();
    // True when DI returned pixel coordinates (images); false when inches (PDFs)
    public bool IsPixelUnit { get; set; }
}

public class RedactRequest
{
    public string SessionId { get; set; } = string.Empty;
    public List<string> SelectedEntityIds { get; set; } = new();
}

public class PreviewRequest
{
    public string SessionId { get; set; } = string.Empty;
    public List<string> SelectedEntityIds { get; set; } = new();
}

public class SessionData
{
    public string SessionId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public List<PiiEntityResult> Entities { get; set; } = new();
    // Raw DI words — kept for manual search and image bounding-box lookup
    public List<WordSearchResult> Words { get; set; } = new();

    // Background processing state
    public string Status { get; set; } = "processing"; // processing | ready | error
    public string Phase { get; set; } = string.Empty;  // extracting | detecting | ""
    public string? ErrorMessage { get; set; }
}

public class AddEntityRequest
{
    public string Text { get; set; } = string.Empty;
}

public class WordSearchResult
{
    public string Text { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public double[] Polygon { get; set; } = Array.Empty<double>();
    public bool IsPixelUnit { get; set; }
}
