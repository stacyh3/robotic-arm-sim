# 🦾 Robotic Arm Simulator

A 6-DOF robotic arm simulator with a 3D web UI and a server exposing REST, WebSocket, and MCP interfaces. Control it from C#, Python, TypeScript, or an LLM.

---

## Architecture

```
┌───────────────────────────────────────────────────────┐
│  3D Browser UI  (Three.js — open robotic_arm_6dof_simulator.html)│
└───────────────────────┬───────────────────────────────┘
                        │ WebSocket ws://localhost:3000/ws
┌───────────────────────────────────────────────────────┐
│ ASP.NET Core Server (src/RoboticArmServer/Program.cs) │
│                                                       │
│  REST  http://localhost:3000/api/arm/*                │
│  WS    ws://localhost:3000/ws                         │
│  MCP   http://localhost:3000/mcp   (stateless)        │
│  MCP   http://localhost:3000/mcp/sse  (SSE transport) │
└──────────┬───────────────┬─────────────────┬──────────┘
           │               │                 │
     C# Client       Python Client     LLM / Claude
   (src/RoboticArmClient) (src/python/robotic_arm_client) (via MCP tool server)
```

---

## Quick Start

### 1. Run the server

```bash
dotnet run --project src/RoboticArmServer/RoboticArmServer.csproj
```

The server starts at `http://localhost:3000`.

### 2. Open the 3D UI

Open `robotic_arm_6dof_simulator.html` in a browser (or use the Claude artifact above).  
Click **CONNECT** in the sidebar to link the UI to the server via WebSocket.

---

## REST API

| Method | Endpoint                | Body / Notes |
|--------|-------------------------|--------------|
| GET    | `/api/arm/state`        | Full arm state + end-effector FK |
| GET    | `/api/arm/sensors`      | Per-joint torque, velocity, temp; power; gripper |
| GET    | `/api/arm/joints/defs`  | Joint limits and axis definitions |
| POST   | `/api/arm/joint/:id`    | `{ "angle": 45 }` — set one joint (0–5) |
| POST   | `/api/arm/joints`       | `{ "angles": [0,40,-90,50,0,20] }` — set all |
| POST   | `/api/arm/home`         | Move to home position |
| POST   | `/api/arm/zero`         | All joints to 0° |
| POST   | `/api/arm/mode`         | `{ "mode": "manual" | "auto" | "hold" }` |
| POST   | `/api/arm/sequence`     | `{ "steps": [{ "joints": [...], "dwell_ms": 500 }] }` |

**Joint index map:**

| ID | Name        | Range |
|----|-------------|-------|
| 0  | Base        | ±180° |
| 1  | Shoulder    | −120° → +80° |
| 2  | Elbow       | ±135° |
| 3  | Wrist Pitch | ±90° |
| 4  | Wrist Roll  | ±180° |
| 5  | Gripper     | 0° → 80° |

---

## WebSocket

Connect to `ws://localhost:3000/ws`.

**Send (client → server):**
```json
{ "type": "set_joint",  "id": 0, "angle": 45 }
{ "type": "set_joints", "angles": [0,40,-90,50,0,20] }
{ "type": "home" }
{ "type": "get_sensors" }
```

**Receive (server → client):**
```json
{ "type": "state",    "data": { "joints": [...], "mode": "manual", ... } }
{ "type": "sensors",  "data": { "joints": [...], "gripper": {...}, ... } }
{ "type": "sequence_complete" }
```

---

## MCP (Model Context Protocol)

The server exposes 6 MCP tools so Claude or any other LLM can control the arm directly.

### Stateless (simplest)

```bash
curl -X POST http://localhost:3000/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

### SSE transport (spec-compliant)

1. `GET /mcp/sse` — opens SSE stream, receives `{ uri: "/mcp/message?sessionId=..." }`
2. `POST /mcp/message?sessionId=...` — send JSON-RPC messages

### Available MCP tools

| Tool | Description |
|------|-------------|
| `arm_get_state`   | Read joints, mode, end-effector position |
| `arm_set_joint`   | Set one joint by index |
| `arm_set_joints`  | Set all 6 joints |
| `arm_home`        | Move to home |
| `arm_get_sensors` | Read all sensor data |
| `arm_run_sequence`| Execute a multi-step motion sequence |

### Connecting Claude to the arm via MCP

Add to your `claude_desktop_config.json` (or equivalent MCP client config):

```json
{
  "mcpServers": {
    "robotic-arm": {
      "url": "http://localhost:3000/mcp/sse"
    }
  }
}
```

Then you can say to Claude:
> "Move the arm's base to 45 degrees, raise the shoulder to 60, and close the gripper."

---

## C# Client

```csharp
// No NuGet needed - uses System.Net.Http + System.Net.WebSockets
await using var arm = new RoboticArmClient("http://localhost:3000");

var state = await arm.GetStateAsync();
await arm.SetJointAsync(0, 45);          // Base to 45 deg
await arm.SetJointsAsync([0,30,-60,40,0,20]);
await arm.HomeAsync();

var sensors = await arm.GetSensorsAsync();
foreach (var j in sensors.Joints)
    Console.WriteLine($"{j.Name}: {j.AngleDeg:F1} deg {j.TorqueNm:F3}Nm");

// WebSocket - live control
await using var ws = await arm.ConnectWebSocketAsync(s =>
    Console.WriteLine($"Joints: {string.Join(",", s.Joints)}"));
await ws.SetJointAsync(1, 60);
await ws.HomeAsync();
```

Library project: `src/RoboticArmClient/RoboticArmClient.csproj`

Demo console app: `src/RoboticArmClient.Demo/RoboticArmClient.Demo.csproj`

Run the demo with:

```bash
dotnet run --project src/RoboticArmClient.Demo/RoboticArmClient.Demo.csproj
```

---

## Python Client

```python
pip install httpx websockets
```

```python
from robotic_arm_client import RoboticArmClient

with RoboticArmClient("http://localhost:3000") as arm:
    arm.home()
    arm.set_joint(0, 45)
    s = arm.get_sensors()
    for j in s.joints:
        print(f"{j.name}: {j.angle_deg:.1f} deg {j.torque_nm:.3f}Nm")

    # Sequence
    arm.run_sequence([
        {"joints": [-45, 30, -60, 40, 0, 60], "dwell_ms": 600},
        {"joints": [  0, 40, -90, 50, 0, 20], "dwell_ms": 600},
        {"joints": [ 45, 30, -60, 40, 0, 60], "dwell_ms": 600},
    ])
```

Library module: `src/python/robotic_arm_client`

Demo script: `src/python/demo_client.py`

Run the demo with:

```bash
python src/python/demo_client.py
```

For async usage use `AsyncRoboticArmClient` with the same API but `await`.

---

## File Layout

```
RoboticArmSim/
├── src/
│   ├── RoboticArmServer/            ← ASP.NET Core server (REST + WS + MCP)
│   ├── RoboticArmSim.AppHost/       ← Aspire AppHost
│   ├── RoboticArmSim.ServiceDefaults/
│   ├── RoboticArmClient/            ← C# reusable client library
│   ├── RoboticArmClient.Demo/       ← C# demo console app
│   └── python/
│       ├── robotic_arm_client/      ← Python reusable client library
│       └── demo_client.py           ← Python demo script
├── tests/
│   └── RoboticArmServer.Tests/
├── RoboticArmSim.slnx
└── README.md
```

---

## Extending

- **Add more sensors** — edit `GetSensors()` in `src/RoboticArmServer/Services/ArmSimulatorService.cs`
- **Inverse kinematics** — add a `POST /api/arm/ik` endpoint accepting `{x,y,z}` target
- **Record & replay** — log `set_joints` calls to a file, replay via `/api/arm/sequence`
- **Real hardware** — replace `armState.joints` reads/writes with serial port calls to an Arduino
- **URDF export** — add a `GET /api/arm/urdf` endpoint for ROS integration
