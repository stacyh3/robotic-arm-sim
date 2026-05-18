using RoboticArmSim;

await using var arm = new RoboticArmClient("http://localhost:3000");

Console.WriteLine("=== Robotic Arm Simulator - C# Client Demo ===\n");

var state = await arm.GetStateAsync();
Console.WriteLine($"Current joints: {string.Join(", ", state.Joints.Select(angle => $"{angle:F1} deg"))}");
Console.WriteLine($"End effector:   X={state.EndEffector.X:F3} Y={state.EndEffector.Y:F3} Z={state.EndEffector.Z:F3}\n");

Console.WriteLine("Moving to home...");
await arm.HomeAsync();

Console.WriteLine("Setting base to 45 deg...");
await arm.SetJointAsync(0, 45);

var sensors = await arm.GetSensorsAsync();
Console.WriteLine("\nSensor readings:");
foreach (var joint in sensors.Joints)
{
    Console.WriteLine($"  {joint.Name,-12} angle={joint.AngleDeg:F2} deg  torque={joint.TorqueNm:F3}Nm  temp={joint.TempC:F1}C");
}

Console.WriteLine($"\nGripper contact: {sensors.Gripper.Contact}");
Console.WriteLine($"Power: {sensors.Power.VoltageV:F2}V @ {sensors.Power.CurrentA:F3}A");

Console.WriteLine("\nRunning wave sequence via WebSocket...");
await using var ws = await arm.ConnectWebSocketAsync(update =>
    Console.WriteLine($"  [WS] joints: {string.Join(", ", update.Joints.Select(angle => $"{angle:F0} deg"))}"));

await ws.SetJointsAsync([-60d, 20d, -60d, 30d, 0d, 40d]);
await Task.Delay(600);
await ws.SetJointsAsync([0d, 40d, -90d, 50d, 0d, 20d]);
await Task.Delay(600);
await ws.SetJointsAsync([60d, 20d, -60d, 30d, 0d, 40d]);
await Task.Delay(600);
await ws.HomeAsync();

Console.WriteLine("\nDone.");
