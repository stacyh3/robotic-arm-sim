import pathlib
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from robotic_arm_client import RoboticArmClient


def main() -> None:
    print("=== Robotic Arm Simulator - Python Client Demo ===\n")

    with RoboticArmClient("http://localhost:3000") as arm:
        state = arm.get_state()
        print(f"Joints:       {[f'{angle:.1f} deg' for angle in state.joints]}")
        print(
            f"End effector: X={state.end_effector.x:.3f} "
            f"Y={state.end_effector.y:.3f} Z={state.end_effector.z:.3f}\n"
        )

        print("Moving to home...")
        arm.home()
        time.sleep(0.3)

        print("Setting base to 45 deg...")
        arm.set_joint(0, 45)

        sensors = arm.get_sensors()
        print("\nSensor readings:")
        for joint in sensors.joints:
            print(
                f"  {joint.name:<14} angle={joint.angle_deg:.2f} deg  "
                f"torque={joint.torque_nm:.3f}Nm  temp={joint.temp_c:.1f}C"
            )

        print(f"\nGripper contact: {sensors.gripper.contact}")
        print(f"Power: {sensors.power.voltage_v:.2f}V @ {sensors.power.current_a:.3f}A")

        print("\nRunning wave sequence via WebSocket...")

        def on_update(data: dict) -> None:
            angles = data.get("joints", [])
            print(f"  [WS] {[f'{angle:.0f} deg' for angle in angles]}")

        ws = arm.connect_websocket(on_state=on_update)
        time.sleep(0.5)

        for pose in [
            [-60, 20, -60, 30, 0, 40],
            [0, 40, -90, 50, 0, 20],
            [60, 20, -60, 30, 0, 40],
        ]:
            ws.send_joints(pose)
            time.sleep(0.6)

        ws.home()
        time.sleep(0.5)
        ws.close()

    print("\nDone.")


if __name__ == "__main__":
    main()
