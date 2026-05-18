using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RoboticArmServer.Contracts;
using RoboticArmServer.Mcp;
using RoboticArmServer.Services;

namespace RoboticArmServer.Tests;

public class ArmSimulatorServiceTests
{
    [Fact]
    public void SetJoint_ClampsToJointLimits()
    {
        var service = new ArmSimulatorService();

        var upperOk = service.SetJoint(0, 999);
        var lowerOk = service.SetJoint(5, -999);
        var state = service.GetState();

        Assert.True(upperOk);
        Assert.True(lowerOk);
        Assert.Equal(180, state.Joints[0]);
        Assert.Equal(0, state.Joints[5]);
    }

    [Fact]
    public void SetMode_RejectsInvalidMode()
    {
        var service = new ArmSimulatorService();

        var invalid = service.SetMode("invalid");
        var valid = service.SetMode("auto");

        Assert.False(invalid);
        Assert.True(valid);
        Assert.Equal("auto", service.GetState().Mode);
    }
}

public class McpDispatcherTests
{
    [Fact]
    public void Dispatch_UnknownMethod_ReturnsJsonRpcMethodNotFound()
    {
        var dispatcher = new McpDispatcher(new ArmSimulatorService());

        var response = dispatcher.Dispatch(new McpRpcRequest("2.0", 1, "unknown/method", null));

        Assert.NotNull(response.Error);
        Assert.Equal(-32601, response.Error!.Code);
    }

    [Fact]
    public void Dispatch_ArmSetJoint_UpdatesArmState()
    {
        var service = new ArmSimulatorService();
        var dispatcher = new McpDispatcher(service);

        var response = dispatcher.Dispatch(new McpRpcRequest(
            "2.0",
            1,
            "tools/call",
            new Dictionary<string, object?>
            {
                ["name"] = JsonSerializer.SerializeToElement("arm_set_joint"),
                ["arguments"] = JsonSerializer.SerializeToElement(new { joint_id = 0, angle = 42.0 })
            }));

        Assert.Null(response.Error);
        Assert.Equal(42.0, service.GetState().Joints[0]);
    }
}

public class WebSocketContractTests
{
    [Fact]
    public async Task SendWelcomeAsync_UsesCamelCaseJoints_ForGuiCompatibility()
    {
        var manager = new ArmWebSocketManager();
        var socket = new CapturingWebSocket();

        await manager.SendWelcomeAsync(socket, new WelcomeData([1, 2, 3, 4, 5, 6], "manual"), CancellationToken.None);

        var payload = socket.SingleMessage();
        using var doc = JsonDocument.Parse(payload);

        Assert.True(doc.RootElement.TryGetProperty("data", out var data));
        Assert.True(data.TryGetProperty("joints", out _));
        Assert.False(data.TryGetProperty("Joints", out _));
    }

    [Fact]
    public async Task BroadcastStateAsync_UsesCamelCaseJoints_ForGuiCompatibility()
    {
        var manager = new ArmWebSocketManager();
        var socket = new CapturingWebSocket();
        manager.AddClient(socket);

        await manager.BroadcastStateAsync("state", new ArmBroadcastData([10, 20, 30, 40, 50, 60], "manual", false, 123));

        var payload = socket.SingleMessage();
        using var doc = JsonDocument.Parse(payload);

        Assert.True(doc.RootElement.TryGetProperty("data", out var data));
        Assert.True(data.TryGetProperty("joints", out _));
        Assert.False(data.TryGetProperty("Joints", out _));
    }

    private sealed class CapturingWebSocket : WebSocket
    {
        private readonly List<string> _messages = [];
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (messageType == WebSocketMessageType.Text)
            {
                _messages.Add(Encoding.UTF8.GetString(buffer.ToArray()));
            }

            return Task.CompletedTask;
        }

        public string SingleMessage()
        {
            Assert.Single(_messages);
            return _messages[0];
        }
    }
}
