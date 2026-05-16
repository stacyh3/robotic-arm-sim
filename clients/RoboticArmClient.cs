// RoboticArmClient.cs
// .NET 7+ client for the Robotic Arm Simulator
//
// Usage:
//   dotnet add package System.Net.WebSockets.Client
//   (no extra NuGet needed — uses built-in HttpClient + System.Net.WebSockets)
//
// Quick start:
//   var arm = new RoboticArmClient("http://localhost:3000");
//   await arm.HomeAsync();
//   await arm.SetJointAsync(0, 45);
//   var sensors = await arm.GetSensorsAsync();

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoboticArmSim;

// ── DTOs ─────────────────────────────────────────────────────────────────────

public record JointDef(string Name, string Axis, double Min, double Max);

public record EndEffector(
    double X, double Y, double Z,
    double YawDeg, double PitchDeg, double RollDeg);

public record ArmState(
    double[]   Joints,
    JointDef[] JointDefs,
    string     Mode,
    bool       IsMoving,
    EndEffector EndEffector,
    long       Timestamp);

public record JointSensor(
    int    Id,
    string Name,
    double AngleDeg,
    double TorqueNm,
    double VelocityRads,
    double TempC);

public record GripperSensor(double ApertureDeg, double ForceN, bool Contact);
public record PowerSensor(double VoltageV, double CurrentA);

public record SensorData(
    JointSensor[] Joints,
    EndEffector   EndEffector,
    GripperSensor Gripper,
    PowerSensor   Power,
    long          Timestamp);

public record SequenceStep(double[] Joints, int DwellMs = 500);

// ── REST Client ───────────────────────────────────────────────────────────────

public class RoboticArmClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string     _baseUrl;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public RoboticArmClient(string baseUrl = "http://localhost:3000")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http    = new HttpClient { BaseAddress = new Uri(_baseUrl + "/") };
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    public async Task<ArmState> GetStateAsync(CancellationToken ct = default)
        => await GetAsync<ArmState>("api/arm/state", ct);

    public async Task<SensorData> GetSensorsAsync(CancellationToken ct = default)
        => await GetAsync<SensorData>("api/arm/sensors", ct);

    public async Task<JointDef[]> GetJointDefsAsync(CancellationToken ct = default)
        => await GetAsync<JointDef[]>("api/arm/joints/defs", ct);

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>Set a single joint angle (degrees). jointId: 0=Base … 5=Gripper.</summary>
    public async Task<bool> SetJointAsync(int jointId, double angle, CancellationToken ct = default)
    {
        var result = await PostAsync<JsonElement>("api/arm/joint/" + jointId, new { angle }, ct);
        return result.GetProperty("ok").GetBoolean();
    }

    /// <summary>Set all 6 joint angles at once.</summary>
    public async Task<double[]> SetJointsAsync(double[] angles, CancellationToken ct = default)
    {
        if (angles.Length != 6) throw new ArgumentException("Must provide exactly 6 angles.");
        var result = await PostAsync<JsonElement>("api/arm/joints", new { angles }, ct);
        return result.GetProperty("joints").Deserialize<double[]>(_json)!;
    }

    /// <summary>Move arm to home position.</summary>
    public async Task HomeAsync(CancellationToken ct = default)
        => await PostAsync<JsonElement>("api/arm/home", null, ct);

    /// <summary>Move arm to zero position (all joints at 0°).</summary>
    public async Task ZeroAsync(CancellationToken ct = default)
        => await PostAsync<JsonElement>("api/arm/zero", null, ct);

    /// <summary>Set arm mode: "manual" | "auto" | "hold".</summary>
    public async Task SetModeAsync(string mode, CancellationToken ct = default)
        => await PostAsync<JsonElement>("api/arm/mode", new { mode }, ct);

    /// <summary>Execute a sequence of poses asynchronously on the server.</summary>
    public async Task RunSequenceAsync(IEnumerable<SequenceStep> steps, CancellationToken ct = default)
    {
        var payload = new
        {
            steps = steps.Select(s => new { joints = s.Joints, dwell_ms = s.DwellMs }).ToArray()
        };
        await PostAsync<JsonElement>("api/arm/sequence", payload, ct);
    }

    // ── Convenience helpers ───────────────────────────────────────────────────

    /// <summary>Open gripper fully.</summary>
    public Task OpenGripperAsync(CancellationToken ct = default) => SetJointAsync(5, 80, ct);

    /// <summary>Close gripper fully.</summary>
    public Task CloseGripperAsync(CancellationToken ct = default) => SetJointAsync(5, 0, ct);

    /// <summary>Rotate the base to the given angle.</summary>
    public Task SetBaseAsync(double angle, CancellationToken ct = default) => SetJointAsync(0, angle, ct);

    // ── WebSocket ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Open a live WebSocket connection. The <paramref name="onStateUpdate"/> callback
    /// is invoked whenever the server broadcasts a state change.
    /// Returns an <see cref="ArmWebSocketSession"/> that you can use to push commands
    /// and dispose to close the connection.
    /// </summary>
    public async Task<ArmWebSocketSession> ConnectWebSocketAsync(
        Action<ArmState>? onStateUpdate = null,
        CancellationToken ct = default)
    {
        var wsUrl = _baseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws";
        var session = new ArmWebSocketSession(wsUrl, onStateUpdate);
        await session.ConnectAsync(ct);
        return session;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        var resp = await _http.GetAsync(path, ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return JsonSerializer.Deserialize<T>(stream, _json)!;
    }

    private async Task<T> PostAsync<T>(string path, object? body, CancellationToken ct)
    {
        StringContent? content = null;
        if (body != null)
        {
            var json = JsonSerializer.Serialize(body, _json);
            content  = new StringContent(json, Encoding.UTF8, "application/json");
        }
        var resp = await _http.PostAsync(path, content, ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return JsonSerializer.Deserialize<T>(stream, _json)!;
    }

    public async ValueTask DisposeAsync() => _http.Dispose();
}

// ── WebSocket Session ─────────────────────────────────────────────────────────

public sealed class ArmWebSocketSession : IAsyncDisposable
{
    private readonly ClientWebSocket   _ws = new();
    private readonly string            _url;
    private readonly Action<ArmState>? _onState;
    private readonly CancellationTokenSource _cts = new();

    internal ArmWebSocketSession(string url, Action<ArmState>? onState)
    {
        _url     = url;
        _onState = onState;
    }

    internal async Task ConnectAsync(CancellationToken ct)
    {
        await _ws.ConnectAsync(new Uri(_url), ct);
        _ = ReceiveLoopAsync(_cts.Token);
    }

    public async Task SetJointAsync(int id, double angle, CancellationToken ct = default)
        => await SendAsync(new { type = "set_joint", id, angle }, ct);

    public async Task SetJointsAsync(double[] angles, CancellationToken ct = default)
        => await SendAsync(new { type = "set_joints", angles }, ct);

    public async Task HomeAsync(CancellationToken ct = default)
        => await SendAsync(new { type = "home" }, ct);

    public async Task RequestSensorsAsync(CancellationToken ct = default)
        => await SendAsync(new { type = "get_sensors" }, ct);

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buf = new byte[8192];
        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buf, ct);
                    sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;

                try
                {
                    using var doc  = JsonDocument.Parse(sb.ToString());
                    var type       = doc.RootElement.GetProperty("type").GetString();
                    var data       = doc.RootElement.GetProperty("data");

                    if ((type == "state" || type == "welcome") && _onState != null)
                    {
                        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        // Build a minimal ArmState from the WS payload
                        var joints = data.GetProperty("joints").Deserialize<double[]>()!;
                        var mode   = data.GetProperty("mode").GetString() ?? "manual";
                        // EndEffector not in WS broadcast, compute locally if needed
                        _onState(new ArmState(joints, Array.Empty<JointDef>(), mode, false,
                            new EndEffector(0, 0, 0, 0, 0, 0), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    }
                }
                catch { /* ignore malformed frames */ }
            }
        }
        catch (OperationCanceledException) { /* expected on dispose */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_ws.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        _ws.Dispose();
    }
}

// ── Demo program ───────────────────────────────────────────────────────────────

// Uncomment to run as a standalone demo:
//
// using RoboticArmSim;
//
// await using var arm = new RoboticArmClient("http://localhost:3000");
//
// Console.WriteLine("=== Robotic Arm Simulator — C# Client Demo ===\n");
//
// var state = await arm.GetStateAsync();
// Console.WriteLine($"Current joints: {string.Join(", ", state.Joints.Select(a => $"{a:F1}°"))}");
// Console.WriteLine($"End effector:   X={state.EndEffector.X:F3} Y={state.EndEffector.Y:F3} Z={state.EndEffector.Z:F3}\n");
//
// Console.WriteLine("Moving to home...");
// await arm.HomeAsync();
//
// Console.WriteLine("Setting base to 45°...");
// await arm.SetJointAsync(0, 45);
//
// var sensors = await arm.GetSensorsAsync();
// Console.WriteLine("\nSensor readings:");
// foreach (var j in sensors.Joints)
//     Console.WriteLine($"  {j.Name,-12} angle={j.AngleDeg:F2}°  torque={j.TorqueNm:F3}Nm  temp={j.TempC:F1}°C");
//
// Console.WriteLine($"\nGripper contact: {sensors.Gripper.Contact}");
// Console.WriteLine($"Power: {sensors.Power.VoltageV:F2}V @ {sensors.Power.CurrentA:F3}A");
//
// Console.WriteLine("\nRunning wave sequence via WebSocket...");
// await using var ws = await arm.ConnectWebSocketAsync(s =>
//     Console.WriteLine($"  [WS] joints: {string.Join(", ", s.Joints.Select(a => $"{a:F0}°"))}"));
//
// await ws.SetJointsAsync(new double[] { -60, 20, -60, 30, 0, 40 });
// await Task.Delay(600);
// await ws.SetJointsAsync(new double[] { 0, 40, -90, 50, 0, 20 });
// await Task.Delay(600);
// await ws.SetJointsAsync(new double[] { 60, 20, -60, 30, 0, 40 });
// await Task.Delay(600);
// await ws.HomeAsync();
//
// Console.WriteLine("\nDone.");
