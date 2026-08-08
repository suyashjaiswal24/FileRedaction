using System.Collections.Concurrent;
using FileRedaction.Models;

namespace FileRedaction.Services;

public class AudioSessionStore
{
    private readonly ConcurrentDictionary<string, AudioSessionData> _sessions = new();

    public AudioSessionData Create(string filePath, string originalFileName)
    {
        var session = new AudioSessionData
        {
            SessionId = Guid.NewGuid().ToString("N"),
            FilePath = filePath,
            OriginalFileName = originalFileName,
            Status = "processing"
        };
        _sessions[session.SessionId] = session;
        return session;
    }

    public AudioSessionData? Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : null;
}
