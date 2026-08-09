using System.Collections.Concurrent;
using FileRedaction.Models;

namespace FileRedaction.Services;

public class VideoSessionStore
{
    private readonly ConcurrentDictionary<string, VideoSessionData> _sessions = new();

    public VideoSessionData Create(string originalFileName)
    {
        var session = new VideoSessionData
        {
            SessionId = Guid.NewGuid().ToString("N"),
            OriginalFileName = originalFileName,
            Status = "processing",
            Phase = "uploading_to_sr"
        };
        _sessions[session.SessionId] = session;
        return session;
    }

    public VideoSessionData? Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : null;
}
