using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RoboticArmServer.Api;
using RoboticArmServer.Contracts;
using RoboticArmServer.Mcp;
using RoboticArmServer.Services;

var builder = WebApplication.CreateBuilder(args);
var webJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

builder.AddServiceDefaults();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .AllowAnyOrigin()
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services.AddSingleton<ArmSimulatorService>();
builder.Services.AddSingleton<ArmWebSocketManager>();
builder.Services.AddSingleton<McpSessionStore>();
builder.Services.AddSingleton<McpDispatcher>();

var app = builder.Build();

app.UseCors();
app.UseWebSockets();

var armService = app.Services.GetRequiredService<ArmSimulatorService>();
var wsManager = app.Services.GetRequiredService<ArmWebSocketManager>();

armService.StateChanged += type => wsManager.BroadcastStateAsync(type, armService.GetBroadcastData());

app.MapGet("/", () => Results.Ok(new
{
    name = "robotic-arm-server",
    rest = "/api/arm/*",
    ws = "/ws",
    mcp = "/mcp"
}));

app.MapArmApi();

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket requests only.");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var clientId = wsManager.AddClient(socket);

    await wsManager.SendWelcomeAsync(socket, armService.GetWelcomeData(), context.RequestAborted);

    var buffer = new byte[8192];

    try
    {
        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            var payload = await ReceiveTextWebSocketMessageAsync(socket, buffer, wsManager, context.RequestAborted);
            if (payload is null)
            {
                break;
            }

            if (payload.Length == 0)
            {
                continue;
            }

            await HandleWebSocketMessageAsync(payload, socket, armService, wsManager, context.RequestAborted);
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected or request cancelled - this is expected
    }
    catch (WebSocketException)
    {
        // WebSocket error during communication - this is expected
    }
    finally
    {
        wsManager.RemoveClient(clientId);
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
        }
    }
});

app.MapGet("/mcp/sse", async (HttpContext context, McpSessionStore sessions) =>
{
    var sessionId = Guid.NewGuid().ToString("N");
    sessions.Add(sessionId, context.Response);

    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";

    var endpointEvent = JsonSerializer.Serialize(new { uri = $"/mcp/message?sessionId={sessionId}" }, webJsonOptions);
    await context.Response.WriteAsync($"event: endpoint\ndata: {endpointEvent}\n\n", context.RequestAborted);
    await context.Response.Body.FlushAsync(context.RequestAborted);

    try
    {
        await Task.Delay(Timeout.Infinite, context.RequestAborted);
    }
    catch (OperationCanceledException)
    {
        // client disconnected
    }
    finally
    {
        sessions.Remove(sessionId);
    }
});

app.MapPost("/mcp/message", async (HttpContext context, McpSessionStore sessions, McpDispatcher dispatcher, McpRpcRequest rpc) =>
{
    var sessionId = context.Request.Query["sessionId"].ToString();

    var response = dispatcher.Dispatch(rpc);
    var sseResponse = string.IsNullOrWhiteSpace(sessionId) ? null : sessions.Get(sessionId);

    if (sseResponse is not null)
    {
        var messageJson = JsonSerializer.Serialize(response, webJsonOptions);
        await sseResponse.WriteAsync($"event: message\ndata: {messageJson}\n\n", context.RequestAborted);
        await sseResponse.Body.FlushAsync(context.RequestAborted);
        return Results.StatusCode(StatusCodes.Status202Accepted);
    }

    return Results.Json(response);
});

app.MapPost("/mcp", (McpDispatcher dispatcher, McpRpcRequest rpc) =>
{
    var response = dispatcher.Dispatch(rpc);
    return Results.Json(response);
});

app.MapDefaultEndpoints();

app.Run("http://0.0.0.0:3000");

static async Task HandleWebSocketMessageAsync(
    string payload,
    WebSocket socket,
    ArmSimulatorService armService,
    ArmWebSocketManager wsManager,
    CancellationToken cancellationToken)
{
    try
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            await wsManager.SendErrorAsync(socket, "Invalid message type.", cancellationToken);
            return;
        }

        var type = typeElement.GetString();

        switch (type)
        {
            case "set_joint":
                {
                    var id = root.GetProperty("id").GetInt32();
                    var angle = root.GetProperty("angle").GetDouble();
                    if (!armService.SetJoint(id, angle))
                    {
                        await wsManager.SendErrorAsync(socket, "Joint id must be 0-5.", cancellationToken);
                    }

                    break;
                }
            case "set_joints":
                {
                    var angles = root.GetProperty("angles")
                        .EnumerateArray()
                        .Select(x => x.GetDouble())
                        .ToArray();

                    if (!armService.SetJoints(angles))
                    {
                        await wsManager.SendErrorAsync(socket, "angles must be array of 6 numbers.", cancellationToken);
                    }

                    break;
                }
            case "home":
                armService.Home();
                break;
            case "get_sensors":
                await wsManager.SendSensorsAsync(socket, armService.GetSensors(), cancellationToken);
                break;
            default:
                await wsManager.SendErrorAsync(socket, $"Unknown message type: {type}", cancellationToken);
                break;
        }
    }
    catch (Exception ex)
    {
        await wsManager.SendErrorAsync(socket, ex.Message, cancellationToken);
    }
}

static async Task<string?> ReceiveTextWebSocketMessageAsync(
    WebSocket socket,
    byte[] buffer,
    ArmWebSocketManager wsManager,
    CancellationToken cancellationToken)
{
    const int maxMessageBytes = 256 * 1024;

    using var messageBuffer = new MemoryStream();

    while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
    {
        var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            return null;
        }

        if (result.Count > 0)
        {
            messageBuffer.Write(buffer, 0, result.Count);

            if (messageBuffer.Length > maxMessageBytes)
            {
                await wsManager.SendErrorAsync(socket, $"WebSocket message too large (max {maxMessageBytes} bytes).", cancellationToken);
                return string.Empty;
            }
        }

        if (!result.EndOfMessage)
        {
            continue;
        }

        if (result.MessageType != WebSocketMessageType.Text)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);
    }

    return null;
}
