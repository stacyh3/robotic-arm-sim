using System.Collections.Concurrent;

namespace RoboticArmServer.Services;

public sealed class McpSessionStore
{
    private readonly ConcurrentDictionary<string, HttpResponse> _sessions = new();

    public void Add(string sessionId, HttpResponse response) => _sessions[sessionId] = response;

    public HttpResponse? Get(string sessionId) => _sessions.TryGetValue(sessionId, out var response) ? response : null;

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);
}
