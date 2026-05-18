using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RoboticArmServer.Contracts;

namespace RoboticArmServer.Services;

public sealed class ArmWebSocketManager
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Guid AddClient(WebSocket socket)
    {
        var id = Guid.NewGuid();
        _clients[id] = socket;
        return id;
    }

    public void RemoveClient(Guid id)
    {
        _clients.TryRemove(id, out _);
    }

    public Task SendWelcomeAsync(WebSocket socket, WelcomeData data, CancellationToken cancellationToken)
        => SendAsync(socket, new { type = "welcome", data }, cancellationToken);

    public Task SendSensorsAsync(WebSocket socket, SensorsResponse sensors, CancellationToken cancellationToken)
        => SendAsync(socket, new { type = "sensors", data = sensors }, cancellationToken);

    public Task SendErrorAsync(WebSocket socket, string message, CancellationToken cancellationToken)
        => SendAsync(socket, new { type = "error", message }, cancellationToken);

    public async Task BroadcastStateAsync(string type, ArmBroadcastData state)
    {
        var payload = JsonSerializer.Serialize(new { type, data = state }, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);
        var segment = new ArraySegment<byte>(bytes);

        var stale = new List<Guid>();

        foreach (var pair in _clients)
        {
            var socket = pair.Value;
            if (socket.State != WebSocketState.Open)
            {
                stale.Add(pair.Key);
                continue;
            }

            try
            {
                await socket.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                stale.Add(pair.Key);
            }
        }

        foreach (var id in stale)
        {
            _clients.TryRemove(id, out _);
        }
    }

    private static Task SendAsync(WebSocket socket, object message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(message, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);
        return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }
}
