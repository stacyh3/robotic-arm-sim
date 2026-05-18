import asyncio
import json
import threading
from dataclasses import dataclass
from typing import Callable, Optional

import httpx

try:
    import websockets
    _WS_AVAILABLE = True
except ImportError:
    _WS_AVAILABLE = False


@dataclass
class EndEffector:
    x: float
    y: float
    z: float
    yaw_deg: float
    pitch_deg: float
    roll_deg: float


@dataclass
class JointSensor:
    id: int
    name: str
    angle_deg: float
    torque_nm: float
    velocity_rads: float
    temp_c: float


@dataclass
class GripperSensor:
    aperture_deg: float
    force_n: float
    contact: bool


@dataclass
class PowerSensor:
    voltage_v: float
    current_a: float


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


def _parse_ee(payload: dict) -> EndEffector:
    return EndEffector(
        x=payload.get("x", 0),
        y=payload.get("y", 0),
        z=payload.get("z", 0),
        yaw_deg=payload.get("yaw_deg", 0),
        pitch_deg=payload.get("pitch_deg", 0),
        roll_deg=payload.get("roll_deg", 0),
    )


class RoboticArmClient:
    def __init__(self, base_url: str = "http://localhost:3000", timeout: float = 10.0):
        self.base_url = base_url.rstrip("/")
        self._http = httpx.Client(base_url=self.base_url + "/", timeout=timeout)

    def get_state(self) -> ArmState:
        payload = self._get("api/arm/state")
        ee = _parse_ee(payload.get("endEffector") or payload.get("end_effector", {}))
        return ArmState(
            joints=payload["joints"],
            mode=payload.get("mode", "manual"),
            is_moving=payload.get("isMoving", False),
            end_effector=ee,
            timestamp=payload.get("timestamp", 0),
        )

    def get_sensors(self) -> SensorData:
        payload = self._get("api/arm/sensors")
        joints = [
            JointSensor(
                id=joint["id"],
                name=joint["name"],
                angle_deg=joint["angle_deg"],
                torque_nm=joint["torque_nm"],
                velocity_rads=joint["velocity_rads"],
                temp_c=joint["temp_c"],
            )
            for joint in payload["joints"]
        ]

        return SensorData(
            joints=joints,
            end_effector=_parse_ee(payload["endEffector"]),
            gripper=GripperSensor(**{k.lower(): v for k, v in payload["gripper"].items()}),
            power=PowerSensor(voltage_v=payload["power"]["voltage_v"], current_a=payload["power"]["current_a"]),
            timestamp=payload["timestamp"],
        )

    def set_joint(self, joint_id: int, angle: float) -> bool:
        response = self._post(f"api/arm/joint/{joint_id}", {"angle": angle})
        return response.get("ok", False)

    def set_joints(self, angles: list[float]) -> list[float]:
        if len(angles) != 6:
            raise ValueError("Must provide exactly 6 angles.")
        response = self._post("api/arm/joints", {"angles": angles})
        return response["joints"]

    def home(self):
        self._post("api/arm/home", {})

    def zero(self):
        self._post("api/arm/zero", {})

    def set_mode(self, mode: str):
        self._post("api/arm/mode", {"mode": mode})

    def run_sequence(self, steps: list[dict]):
        self._post("api/arm/sequence", {"steps": steps})

    def open_gripper(self):
        self.set_joint(5, 80)

    def close_gripper(self):
        self.set_joint(5, 0)

    def set_base(self, angle: float):
        self.set_joint(0, angle)

    def connect_websocket(self, on_state: Optional[Callable[[dict], None]] = None) -> "ArmWebSocketThread":
        if not _WS_AVAILABLE:
            raise RuntimeError("Install 'websockets' to use WebSocket: pip install websockets")

        ws_url = self.base_url.replace("http://", "ws://").replace("https://", "wss://") + "/ws"
        thread = ArmWebSocketThread(ws_url, on_state)
        thread.start()
        return thread

    def _get(self, path: str) -> dict:
        response = self._http.get(path)
        response.raise_for_status()
        return response.json()

    def _post(self, path: str, body: dict) -> dict:
        response = self._http.post(path, json=body)
        response.raise_for_status()
        return response.json()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self._http.close()


class AsyncRoboticArmClient:
    def __init__(self, base_url: str = "http://localhost:3000", timeout: float = 10.0):
        self.base_url = base_url.rstrip("/")
        self._http = httpx.AsyncClient(base_url=self.base_url + "/", timeout=timeout)

    async def get_state(self) -> ArmState:
        payload = await self._get("api/arm/state")
        return ArmState(
            joints=payload["joints"],
            mode=payload.get("mode", "manual"),
            is_moving=payload.get("isMoving", False),
            end_effector=_parse_ee(payload.get("endEffector", {})),
            timestamp=payload.get("timestamp", 0),
        )

    async def get_sensors(self) -> SensorData:
        payload = await self._get("api/arm/sensors")
        return SensorData(
            joints=[
                JointSensor(
                    id=joint["id"],
                    name=joint["name"],
                    angle_deg=joint["angle_deg"],
                    torque_nm=joint["torque_nm"],
                    velocity_rads=joint["velocity_rads"],
                    temp_c=joint["temp_c"],
                )
                for joint in payload["joints"]
            ],
            end_effector=_parse_ee(payload["endEffector"]),
            gripper=GripperSensor(
                aperture_deg=payload["gripper"]["aperture_deg"],
                force_n=payload["gripper"]["force_n"],
                contact=payload["gripper"]["contact"],
            ),
            power=PowerSensor(voltage_v=payload["power"]["voltage_v"], current_a=payload["power"]["current_a"]),
            timestamp=payload["timestamp"],
        )

    async def set_joint(self, joint_id: int, angle: float) -> bool:
        response = await self._post(f"api/arm/joint/{joint_id}", {"angle": angle})
        return response.get("ok", False)

    async def set_joints(self, angles: list[float]) -> list[float]:
        response = await self._post("api/arm/joints", {"angles": angles})
        return response["joints"]

    async def home(self):
        await self._post("api/arm/home", {})

    async def zero(self):
        await self._post("api/arm/zero", {})

    async def open_gripper(self):
        await self.set_joint(5, 80)

    async def close_gripper(self):
        await self.set_joint(5, 0)

    async def _get(self, path: str) -> dict:
        response = await self._http.get(path)
        response.raise_for_status()
        return response.json()

    async def _post(self, path: str, body: dict) -> dict:
        response = await self._http.post(path, json=body)
        response.raise_for_status()
        return response.json()

    async def __aenter__(self):
        return self

    async def __aexit__(self, *_):
        await self._http.aclose()


class ArmWebSocketThread(threading.Thread):
    def __init__(self, url: str, on_state: Optional[Callable[[dict], None]]):
        super().__init__(daemon=True)
        self._url = url
        self._on_state = on_state
        self._loop = asyncio.new_event_loop()
        self._ws = None
        self._queue: asyncio.Queue = None  # type: ignore

    def run(self):
        self._loop.run_until_complete(self._main())

    async def _main(self):
        self._queue = asyncio.Queue()
        async with websockets.connect(self._url) as ws:
            self._ws = ws
            recv_task = asyncio.create_task(self._recv_loop(ws))
            send_task = asyncio.create_task(self._send_loop(ws))
            done, pending = await asyncio.wait([recv_task, send_task], return_when=asyncio.FIRST_COMPLETED)
            for task in pending:
                task.cancel()

    async def _recv_loop(self, ws):
        async for raw in ws:
            try:
                message = json.loads(raw)
                if message.get("type") in ("state", "welcome") and self._on_state:
                    self._on_state(message.get("data", {}))
            except Exception:
                pass

    async def _send_loop(self, ws):
        while True:
            payload = await self._queue.get()
            if payload is None:
                break
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
