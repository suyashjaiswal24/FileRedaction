using System.Collections.Concurrent;
using FileRedaction.Models;

namespace FileRedaction.Services;

public class SessionStore
{
    private readonly ConcurrentDictionary<string, SessionData> _sessions = new();

    public void Set(string sessionId, SessionData data) => _sessions[sessionId] = data;

    public SessionData? Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var data) ? data : null;

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);
}
