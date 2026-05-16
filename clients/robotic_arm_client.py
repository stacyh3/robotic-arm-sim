"""
robotic_arm_client.py
Python client for the Robotic Arm Simulator.

Install:   pip install httpx websockets
Quick start:
    arm = RoboticArmClient("http://localhost:3000")
    arm.home()
    arm.set_joint(0, 45)
    print(arm.get_sensors())
"""

import asyncio
import json
import threading
from dataclasses import dataclass, field
from typing import Callable, Optional

import httpx

# Optional: pip install websockets
try:
    import websockets
    _WS_AVAILABLE = True
except ImportError:
    _WS_AVAILABLE = False


# ── Data classes ───────────────────────────────────────────────────────────────

@dataclass
class EndEffector:
    x: float; y: float; z: float
    yaw_deg: float; pitch_deg: float; roll_deg: float

@dataclass
class JointSensor:
    id: int; name: str
    angle_deg: float; torque_nm: float
    velocity_rads: float; temp_c: float

@dataclass
class GripperSensor:
    aperture_deg: float; force_n: float; contact: bool

@dataclass
class PowerSensor:
    voltage_v: float; current_a: float

@dataclass
class SensorData:
    joints: list[JointSensor]
    end_effector: EndEffector
    gripper: GripperSensor
    power: PowerSensor
    timestamp: int

@dataclass
class ArmState:
    joints: list[float]
    mode: str
    is_moving: bool
    end_effector: EndEffector
    timestamp: int


def _parse_ee(d: dict) -> EndEffector:
    return EndEffector(
        x=d.get("x", 0), y=d.get("y", 0), z=d.get("z", 0),
        yaw_deg=d.get("yaw_deg", 0), pitch_deg=d.get("pitch_deg", 0),
        roll_deg=d.get("roll_deg", 0),
    )


# ── Sync REST Client ────────────────────────────────────────────────────────────

class RoboticArmClient:
    """Synchronous HTTP client for the Robotic Arm Simulator."""

    def __init__(self, base_url: str = "http://localhost:3000", timeout: float = 10.0):
        self.base_url = base_url.rstrip("/")
        self._http = httpx.Client(base_url=self.base_url + "/", timeout=timeout)

    # ── Read ──────────────────────────────────────────────────────────────────

    def get_state(self) -> ArmState:
        d = self._get("api/arm/state")
        ee = _parse_ee(d.get("endEffector") or d.get("end_effector", {}))
        return ArmState(
            joints=d["joints"], mode=d.get("mode", "manual"),
            is_moving=d.get("isMoving", False), end_effector=ee,
            timestamp=d.get("timestamp", 0),
        )

    def get_sensors(self) -> SensorData:
        d = self._get("api/arm/sensors")
        joints = [JointSensor(
            id=j["id"], name=j["name"],
            angle_deg=j["angle_deg"], torque_nm=j["torque_nm"],
            velocity_rads=j["velocity_rads"], temp_c=j["temp_c"],
        ) for j in d["joints"]]
        return SensorData(
            joints=joints,
            end_effector=_parse_ee(d["endEffector"]),
            gripper=GripperSensor(**{k.lower(): v for k, v in d["gripper"].items()}),
            power=PowerSensor(voltage_v=d["power"]["voltage_v"], current_a=d["power"]["current_a"]),
            timestamp=d["timestamp"],
        )

    # ── Write ─────────────────────────────────────────────────────────────────

    def set_joint(self, joint_id: int, angle: float) -> bool:
        """Set a single joint angle. joint_id: 0=Base … 5=Gripper."""
        r = self._post(f"api/arm/joint/{joint_id}", {"angle": angle})
        return r.get("ok", False)

    def set_joints(self, angles: list[float]) -> list[float]:
        """Set all 6 joint angles at once."""
        if len(angles) != 6:
            raise ValueError("Must provide exactly 6 angles.")
        r = self._post("api/arm/joints", {"angles": angles})
        return r["joints"]

    def home(self):
        """Move arm to home position."""
        self._post("api/arm/home", {})

    def zero(self):
        """Move arm to zero position (all joints at 0°)."""
        self._post("api/arm/zero", {})

    def set_mode(self, mode: str):
        """Set arm mode: 'manual' | 'auto' | 'hold'."""
        self._post("api/arm/mode", {"mode": mode})

    def run_sequence(self, steps: list[dict]):
        """
        Execute a sequence of poses. Each step: {"joints": [...6 floats], "dwell_ms": 500}
        The server runs the sequence asynchronously after responding.
        """
        self._post("api/arm/sequence", {"steps": steps})

    # ── Convenience ───────────────────────────────────────────────────────────

    def open_gripper(self):  self.set_joint(5, 80)
    def close_gripper(self): self.set_joint(5, 0)
    def set_base(self, angle: float): self.set_joint(0, angle)

    # ── WebSocket ─────────────────────────────────────────────────────────────

    def connect_websocket(
        self,
        on_state: Optional[Callable[[dict], None]] = None,
    ) -> "ArmWebSocketThread":
        """
        Open a background WebSocket thread. Returns an ArmWebSocketThread
        with send_joint(), send_joints(), home(), etc. methods.
        """
        if not _WS_AVAILABLE:
            raise RuntimeError("Install 'websockets' to use WebSocket: pip install websockets")
        ws_url = self.base_url.replace("http://", "ws://").replace("https://", "wss://") + "/ws"
        t = ArmWebSocketThread(ws_url, on_state)
        t.start()
        return t

    # ── Internals ─────────────────────────────────────────────────────────────

    def _get(self, path: str) -> dict:
        r = self._http.get(path)
        r.raise_for_status()
        return r.json()

    def _post(self, path: str, body: dict) -> dict:
        r = self._http.post(path, json=body)
        r.raise_for_status()
        return r.json()

    def __enter__(self): return self
    def __exit__(self, *_): self._http.close()


# ── Async REST Client ─────────────────────────────────────────────────────────

class AsyncRoboticArmClient:
    """Async/await HTTP client for the Robotic Arm Simulator."""

    def __init__(self, base_url: str = "http://localhost:3000", timeout: float = 10.0):
        self.base_url = base_url.rstrip("/")
        self._http = httpx.AsyncClient(base_url=self.base_url + "/", timeout=timeout)

    async def get_state(self) -> ArmState:
        d = await self._get("api/arm/state")
        return ArmState(
            joints=d["joints"], mode=d.get("mode", "manual"),
            is_moving=d.get("isMoving", False),
            end_effector=_parse_ee(d.get("endEffector", {})),
            timestamp=d.get("timestamp", 0),
        )

    async def get_sensors(self) -> SensorData:
        d = await self._get("api/arm/sensors")
        return SensorData(
            joints=[JointSensor(id=j["id"], name=j["name"], angle_deg=j["angle_deg"],
                torque_nm=j["torque_nm"], velocity_rads=j["velocity_rads"], temp_c=j["temp_c"])
                for j in d["joints"]],
            end_effector=_parse_ee(d["endEffector"]),
            gripper=GripperSensor(aperture_deg=d["gripper"]["aperture_deg"],
                force_n=d["gripper"]["force_n"], contact=d["gripper"]["contact"]),
            power=PowerSensor(voltage_v=d["power"]["voltage_v"], current_a=d["power"]["current_a"]),
            timestamp=d["timestamp"],
        )

    async def set_joint(self, joint_id: int, angle: float) -> bool:
        r = await self._post(f"api/arm/joint/{joint_id}", {"angle": angle})
        return r.get("ok", False)

    async def set_joints(self, angles: list[float]) -> list[float]:
        r = await self._post("api/arm/joints", {"angles": angles})
        return r["joints"]

    async def home(self): await self._post("api/arm/home", {})
    async def zero(self): await self._post("api/arm/zero", {})
    async def open_gripper(self):  await self.set_joint(5, 80)
    async def close_gripper(self): await self.set_joint(5, 0)

    async def _get(self, path: str) -> dict:
        r = await self._http.get(path)
        r.raise_for_status()
        return r.json()

    async def _post(self, path: str, body: dict) -> dict:
        r = await self._http.post(path, json=body)
        r.raise_for_status()
        return r.json()

    async def __aenter__(self): return self
    async def __aexit__(self, *_): await self._http.aclose()


# ── WebSocket background thread ────────────────────────────────────────────────

class ArmWebSocketThread(threading.Thread):
    def __init__(self, url: str, on_state: Optional[Callable[[dict], None]]):
        super().__init__(daemon=True)
        self._url      = url
        self._on_state = on_state
        self._loop     = asyncio.new_event_loop()
        self._ws       = None
        self._queue: asyncio.Queue = None  # type: ignore

    def run(self):
        self._loop.run_until_complete(self._main())

    async def _main(self):
        self._queue = asyncio.Queue()
        async with websockets.connect(self._url) as ws:
            self._ws = ws
            recv_task = asyncio.create_task(self._recv_loop(ws))
            send_task = asyncio.create_task(self._send_loop(ws))
            done, pending = await asyncio.wait(
                [recv_task, send_task], return_when=asyncio.FIRST_COMPLETED)
            for t in pending: t.cancel()

    async def _recv_loop(self, ws):
        async for raw in ws:
            try:
                msg = json.loads(raw)
                if msg.get("type") in ("state", "welcome") and self._on_state:
                    self._on_state(msg.get("data", {}))
            except Exception: pass

    async def _send_loop(self, ws):
        while True:
            payload = await self._queue.get()
            if payload is None: break
            await ws.send(json.dumps(payload))

    def _send(self, payload: dict):
        self._loop.call_soon_threadsafe(self._queue.put_nowait, payload)

    def send_joint(self, joint_id: int, angle: float):
        self._send({"type": "set_joint", "id": joint_id, "angle": angle})

    def send_joints(self, angles: list[float]):
        self._send({"type": "set_joints", "angles": angles})

    def home(self):
        self._send({"type": "home"})

    def request_sensors(self):
        self._send({"type": "get_sensors"})

    def close(self):
        self._send(None)


# ── CLI demo ───────────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import time

    print("=== Robotic Arm Simulator — Python Client Demo ===\n")

    with RoboticArmClient("http://localhost:3000") as arm:
        state = arm.get_state()
        print(f"Joints:       {[f'{a:.1f}°' for a in state.joints]}")
        print(f"End effector: X={state.end_effector.x:.3f} Y={state.end_effector.y:.3f} Z={state.end_effector.z:.3f}\n")

        print("Moving to home...")
        arm.home()
        time.sleep(0.3)

        print("Setting base to 45°...")
        arm.set_joint(0, 45)

        sensors = arm.get_sensors()
        print("\nSensor readings:")
        for j in sensors.joints:
            print(f"  {j.name:<14} angle={j.angle_deg:.2f}°  "
                  f"torque={j.torque_nm:.3f}Nm  temp={j.temp_c:.1f}°C")
        print(f"\nGripper contact: {sensors.gripper.contact}")
        print(f"Power: {sensors.power.voltage_v:.2f}V @ {sensors.power.current_a:.3f}A")

        print("\nRunning wave sequence via WebSocket...")
        def on_update(data):
            angles = data.get("joints", [])
            print(f"  [WS] {[f'{a:.0f}°' for a in angles]}")

        ws = arm.connect_websocket(on_state=on_update)
        time.sleep(0.5)

        for pose in [
            [-60, 20, -60, 30, 0, 40],
            [  0, 40, -90, 50, 0, 20],
            [ 60, 20, -60, 30, 0, 40],
        ]:
            ws.send_joints(pose)
            time.sleep(0.6)

        ws.home()
        time.sleep(0.5)
        ws.close()

    print("\nDone.")
