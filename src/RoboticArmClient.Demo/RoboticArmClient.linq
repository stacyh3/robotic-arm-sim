<Query Kind="Program">
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Net.WebSockets</Namespace>
  <Namespace>System.Text.Json</Namespace>
  <Namespace>System.Text.Json.Serialization</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

// LINQPad-ready C# Program for the Robotic Arm Simulator.
// Paste into LINQPad and run in "C# Program" mode.


async Task Main()
{
	await using var arm = new RoboticArmClient("http://localhost:3000");

	Console.WriteLine("=== Robotic Arm Simulator - LINQPad Demo ===\n");

	var state = await arm.GetStateAsync();
	Console.WriteLine($"Current joints: {string.Join(", ", state.Joints.Select(a => $"{a:F1} deg"))}");
	if (state.EndEffector is { } ee)
		Console.WriteLine($"End effector:   X={ee.X:F3} Y={ee.Y:F3} Z={ee.Z:F3}\n");
	else
		Console.WriteLine("End effector:   (not available)\n");
		
	await Task.Delay(3000);

	Console.WriteLine("Moving to home...");
	await arm.HomeAsync();
	await Task.Delay(3000);

	Console.WriteLine("Setting base to 45 deg...");
	await arm.SetJointAsync(0, 45);
	await Task.Delay(3000);

	var sensors = await arm.GetSensorsAsync();
	Console.WriteLine("\nSensor readings:");
	foreach (var j in sensors.Joints)
		Console.WriteLine($"  {j.Name,-12} angle={j.AngleDeg:F2} deg  torque={j.TorqueNm:F3}Nm  temp={j.TempC:F1}C");

	Console.WriteLine($"\nGripper contact: {sensors.Gripper.Contact}");
	Console.WriteLine($"Power: {sensors.Power.VoltageV:F2}V @ {sensors.Power.CurrentA:F3}A");

	Console.WriteLine("\nRunning wave sequence via WebSocket...");
	await using var ws = await arm.ConnectWebSocketAsync(s =>
		Console.WriteLine($"  [WS] joints: {string.Join(", ", s.Joints.Select(a => $"{a:F0} deg"))}"));

	await ws.SetJointsAsync(new[] { -60d, 20d, -60d, 30d, 0d, 40d });
	await Task.Delay(600);
	await ws.SetJointsAsync(new[] { 0d, 40d, -90d, 50d, 0d, 20d });
	await Task.Delay(600);
	await ws.SetJointsAsync(new[] { 60d, 20d, -60d, 30d, 0d, 40d });
	await Task.Delay(600);
	await ws.HomeAsync();

	Console.WriteLine("\nDone.");
}

public record JointDef(string Name, string Axis, double Min, double Max);

public record EndEffector(
	double X, double Y, double Z,
	double YawDeg, double PitchDeg, double RollDeg);

public record ArmState(
	double[] Joints,
	JointDef[] JointDefs,
	string Mode,
	bool IsMoving,
	EndEffector? EndEffector,
	long Timestamp);

public record JointSensor(
	int Id,
	string Name,
	double AngleDeg,
	double TorqueNm,
	double VelocityRads,
	double TempC);

public record GripperSensor(double ApertureDeg, double ForceN, bool Contact);
public record PowerSensor(double VoltageV, double CurrentA);

public record SensorData(
	JointSensor[] Joints,
	EndEffector EndEffector,
	GripperSensor Gripper,
	PowerSensor Power,
	long Timestamp);

public record SequenceStep(double[] Joints, int DwellMs = 500);

public class RoboticArmClient : IAsyncDisposable
{
	private readonly HttpClient _http;
	private readonly string _baseUrl;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	public RoboticArmClient(string baseUrl = "http://localhost:3000")
	{
		_baseUrl = baseUrl.TrimEnd('/');
		_http = new HttpClient { BaseAddress = new Uri(_baseUrl + "/") };
	}

	public Task<ArmState> GetStateAsync(CancellationToken ct = default)
		=> GetAsync<ArmState>("api/arm/state", ct);

	public Task<SensorData> GetSensorsAsync(CancellationToken ct = default)
		=> GetAsync<SensorData>("api/arm/sensors", ct);

	public Task<JointDef[]> GetJointDefsAsync(CancellationToken ct = default)
		=> GetAsync<JointDef[]>("api/arm/joints/defs", ct);

	public async Task<bool> SetJointAsync(int jointId, double angle, CancellationToken ct = default)
	{
		var result = await PostAsync<JsonElement>("api/arm/joint/" + jointId, new { angle }, ct);
		return result.GetProperty("ok").GetBoolean();
	}

	public async Task<double[]> SetJointsAsync(double[] angles, CancellationToken ct = default)
	{
		if (angles.Length != 6) throw new ArgumentException("Must provide exactly 6 angles.");
		var result = await PostAsync<JsonElement>("api/arm/joints", new { angles }, ct);
		return result.GetProperty("joints").Deserialize<double[]>(JsonOptions)!;
	}

	public Task HomeAsync(CancellationToken ct = default)
		=> PostAsync<JsonElement>("api/arm/home", null, ct);

	public Task ZeroAsync(CancellationToken ct = default)
		=> PostAsync<JsonElement>("api/arm/zero", null, ct);

	public Task SetModeAsync(string mode, CancellationToken ct = default)
		=> PostAsync<JsonElement>("api/arm/mode", new { mode }, ct);

	public async Task RunSequenceAsync(IEnumerable<SequenceStep> steps, CancellationToken ct = default)
	{
		var payload = new
		{
			steps = steps.Select(s => new { joints = s.Joints, dwell_ms = s.DwellMs }).ToArray()
		};
		await PostAsync<JsonElement>("api/arm/sequence", payload, ct);
	}

	public Task OpenGripperAsync(CancellationToken ct = default) => SetJointAsync(5, 80, ct);

	public Task CloseGripperAsync(CancellationToken ct = default) => SetJointAsync(5, 0, ct);

	public Task SetBaseAsync(double angle, CancellationToken ct = default) => SetJointAsync(0, angle, ct);

	public async Task<ArmWebSocketSession> ConnectWebSocketAsync(
		Action<ArmState>? onStateUpdate = null,
		CancellationToken ct = default)
	{
		var wsUrl = _baseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws";
		var session = new ArmWebSocketSession(wsUrl, onStateUpdate);
		await session.ConnectAsync(ct);
		return session;
	}

	private async Task<T> GetAsync<T>(string path, CancellationToken ct)
	{
		var resp = await _http.GetAsync(path, ct);
		resp.EnsureSuccessStatusCode();
		await using var stream = await resp.Content.ReadAsStreamAsync(ct);
		return JsonSerializer.Deserialize<T>(stream, JsonOptions)!;
	}

	private async Task<T> PostAsync<T>(string path, object? body, CancellationToken ct)
	{
		StringContent? content = null;
		if (body != null)
		{
			var json = JsonSerializer.Serialize(body, JsonOptions);
			content = new StringContent(json, Encoding.UTF8, "application/json");
		}

		var resp = await _http.PostAsync(path, content, ct);
		resp.EnsureSuccessStatusCode();
		await using var stream = await resp.Content.ReadAsStreamAsync(ct);
		return JsonSerializer.Deserialize<T>(stream, JsonOptions)!;
	}

	public ValueTask DisposeAsync()
	{
		_http.Dispose();
		return ValueTask.CompletedTask;
	}
}

public sealed class ArmWebSocketSession : IAsyncDisposable
{
	private readonly ClientWebSocket _ws = new();
	private readonly string _url;
	private readonly Action<ArmState>? _onState;
	private readonly CancellationTokenSource _cts = new();

	internal ArmWebSocketSession(string url, Action<ArmState>? onState)
	{
		_url = url;
		_onState = onState;
	}

	internal async Task ConnectAsync(CancellationToken ct)
	{
		await _ws.ConnectAsync(new Uri(_url), ct);
		_ = ReceiveLoopAsync(_cts.Token);
	}

	public Task SetJointAsync(int id, double angle, CancellationToken ct = default)
		=> SendAsync(new { type = "set_joint", id, angle }, ct);

	public Task SetJointsAsync(double[] angles, CancellationToken ct = default)
		=> SendAsync(new { type = "set_joints", angles }, ct);

	public Task HomeAsync(CancellationToken ct = default)
		=> SendAsync(new { type = "home" }, ct);

	public Task RequestSensorsAsync(CancellationToken ct = default)
		=> SendAsync(new { type = "get_sensors" }, ct);

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
					using var doc = JsonDocument.Parse(sb.ToString());
					var type = doc.RootElement.GetProperty("type").GetString();
					var data = doc.RootElement.GetProperty("data");

					if ((type == "state" || type == "welcome") && _onState != null)
					{
						var joints = data.GetProperty("joints").Deserialize<double[]>()!;
						var mode = data.GetProperty("mode").GetString() ?? "manual";
						_onState(new ArmState(
							joints,
							Array.Empty<JointDef>(),
							mode,
							false,
							new EndEffector(0, 0, 0, 0, 0, 0),
							DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
					}
				}
				catch
				{
					// Ignore malformed frames.
				}
			}
		}
		catch (OperationCanceledException)
		{
			// Expected on dispose.
		}
	}

	public async ValueTask DisposeAsync()
	{
		_cts.Cancel();
		if (_ws.State == WebSocketState.Open)
			await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

		_ws.Dispose();
	}
}
