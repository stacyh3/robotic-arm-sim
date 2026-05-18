# RoboticArmServer (.NET 10)

This is the primary ASP.NET Core server for the simulator, with support for:

- REST API (`/api/arm/*`)
- WebSocket live control (`/ws`)
- MCP JSON-RPC (`/mcp`, `/mcp/sse`, `/mcp/message`)

The REST layer uses modular minimal APIs (`MapGroup`) for clear route organization.

## Run

```bash
dotnet restore
dotnet run --project src/RoboticArmServer/RoboticArmServer.csproj
```

The server listens on `http://localhost:3000`.

## REST routes

- `GET /api/arm/state`
- `GET /api/arm/sensors`
- `GET /api/arm/joints/defs`
- `POST /api/arm/joint/{id}` body: `{ "angle": 45 }`
- `POST /api/arm/joints` body: `{ "angles": [0, 40, -90, 50, 0, 20] }`
- `POST /api/arm/home`
- `POST /api/arm/zero`
- `POST /api/arm/mode` body: `{ "mode": "manual|auto|hold" }`
- `POST /api/arm/sequence` body: `{ "steps": [{ "joints": [...], "dwellMs": 500 }] }`

## Notes

- Joint limits and kinematics behavior mirror the JavaScript implementation.
- Sensor values include light synthetic noise.
- Sequence execution is asynchronous and broadcasts `sequence_complete` on completion.
