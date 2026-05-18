# Node to .NET Migration Checklist (Safe Retirement Plan)

Use this checklist to move from `server/` (Node) to `src/RoboticArmServer/` (.NET) with low risk.

## Current Status (2026-05-17)

Completed in this run:

- [x] Fixed fragmented WebSocket frame handling in the .NET `/ws` loop.
- [x] Fixed WebSocket JSON casing compatibility (`data.joints` etc.) for GUI/client parity.
- [x] Added regression tests for WebSocket payload casing.
- [x] Added core unit tests for `ArmSimulatorService` and `McpDispatcher`.
- [x] Executed test suite successfully (`6 passed, 0 failed`).

Still pending:

- [ ] Full side-by-side contract diff against Node for all REST/WS/MCP payloads.
- [ ] Structured logging and request tracing.
- [ ] Integration smoke tests that run against a live server process.
- [ ] Cutover rollout and monitoring steps.

## Phase 1: Freeze and Baseline

- [ ] Freeze feature changes in `server/` except critical fixes.
- [ ] Declare `src/RoboticArmServer/` as the target implementation for all new features.
- [ ] Capture current API behavior from Node (status codes, error messages, payload shapes).
- [ ] Record baseline latency and throughput for key operations:
- `GET /api/arm/state`
- `GET /api/arm/sensors`
- `POST /api/arm/joints`
- WebSocket message handling and broadcast responsiveness

## Phase 2: Contract Parity

- [ ] Verify all REST endpoints match expected request/response contracts.
- [ ] Verify WebSocket message types are equivalent:
- `welcome`, `state`, `sensors`, `error`, `sequence_complete`
- [ ] Verify MCP methods and tools are equivalent:
- `initialize`, `tools/list`, `tools/call`
- `arm_get_state`, `arm_set_joint`, `arm_set_joints`, `arm_home`, `arm_get_sensors`, `arm_run_sequence`
- [ ] Confirm error semantics are acceptable (JSON-RPC codes/messages and HTTP 400 behavior).

## Phase 3: Reliability Hardening

- [ ] Validate fragmented WebSocket frames are handled (fixed in .NET).
- [ ] Add rate/size safeguards for WS and HTTP payloads as needed.
- [ ] Add structured logging for request IDs, endpoint, status, and latency.
- [ ] Add health endpoint and startup diagnostics.

## Phase 4: Test Coverage

- [ ] Add unit tests for `ArmSimulatorService`:
- Joint clamping
- Mode validation
- Sequence start/complete behavior
- [ ] Add tests for `McpDispatcher`:
- Method/tool dispatch
- Invalid method/tool and invalid arguments
- [ ] Add integration tests for core REST endpoints and one WS flow.

## Phase 5: Cutover

- [ ] Update clients/default configs to point to the .NET server.
- [ ] Run side-by-side smoke test (Node and .NET) in the same environment.
- [ ] Enable .NET as primary in dev, then staging, then production-like environment.
- [ ] Monitor logs/metrics for at least one release cycle.

## Phase 6: Node Retirement

- [ ] Mark `server/` as legacy in docs.
- [ ] Stop new changes to Node implementation.
- [ ] Archive or remove Node runtime/dependencies from CI.
- [ ] Keep Node code only as historical reference if desired.

## Go/No-Go Criteria

Proceed with Node retirement only if all are true:

- [ ] No contract mismatches found in parity verification.
- [ ] No high-severity WS/MCP defects in staging.
- [ ] Test suite passes for .NET server.
- [ ] Observability is sufficient to troubleshoot production issues quickly.
