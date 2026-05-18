using System.Text.Json;
using RoboticArmServer.Contracts;
using RoboticArmServer.Services;

namespace RoboticArmServer.Mcp;

public sealed class McpDispatcher(ArmSimulatorService armService)
{
    private static readonly McpTool[] Tools =
    [
        new(
            "arm_get_state",
            "Get the current joint angles, end-effector position, and mode of the robotic arm.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }),
        new(
            "arm_set_joint",
            "Set a single joint angle by joint index (0=Base, 1=Shoulder, 2=Elbow, 3=WristPitch, 4=WristRoll, 5=Gripper).",
            new
            {
                type = "object",
                properties = new
                {
                    joint_id = new { type = "integer", minimum = 0, maximum = 5, description = "Joint index 0-5" },
                    angle = new { type = "number", description = "Target angle in degrees" }
                },
                required = new[] { "joint_id", "angle" }
            }),
        new(
            "arm_set_joints",
            "Set all 6 joint angles at once. Provide array of 6 degree values.",
            new
            {
                type = "object",
                properties = new
                {
                    angles = new { type = "array", items = new { type = "number" }, minItems = 6, maxItems = 6 }
                },
                required = new[] { "angles" }
            }),
        new(
            "arm_home",
            "Move the arm to its home/rest position.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }),
        new(
            "arm_get_sensors",
            "Read all sensor data: per-joint torque, velocity, temperature, end-effector position, power.",
            new { type = "object", properties = new { }, required = Array.Empty<string>() }),
        new(
            "arm_run_sequence",
            "Execute a sequence of poses. Each step has joints[] array and optional dwell_ms.",
            new
            {
                type = "object",
                properties = new
                {
                    steps = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                joints = new { type = "array", items = new { type = "number" }, minItems = 6, maxItems = 6 },
                                dwell_ms = new { type = "integer", minimum = 0 }
                            },
                            required = new[] { "joints" }
                        }
                    }
                },
                required = new[] { "steps" }
            })
    ];

    public McpRpcResponse Dispatch(McpRpcRequest rpc)
    {
        try
        {
            var result = rpc.Method switch
            {
                "initialize" => new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "robotic-arm-server", version = "1.0.0" }
                },
                "tools/list" => new { tools = Tools },
                "tools/call" => CallTool(rpc.Params ?? new Dictionary<string, object?>()),
                _ => throw new McpDispatchException(-32601, $"Method not found: {rpc.Method}")
            };

            return new McpRpcResponse(rpc.Id, Result: result);
        }
        catch (McpDispatchException ex)
        {
            return new McpRpcResponse(rpc.Id, Error: new McpRpcError(ex.Code, ex.Message));
        }
        catch (Exception ex)
        {
            return new McpRpcResponse(rpc.Id, Error: new McpRpcError(-32603, ex.Message));
        }
    }

    private object CallTool(Dictionary<string, object?> parameters)
    {
        var name = GetString(parameters, "name");
        var args = GetObject(parameters, "arguments");

        return name switch
        {
            "arm_get_state" => TextResult(JsonSerializer.Serialize(new
            {
                joints = armService.GetState().Joints,
                mode = armService.GetState().Mode,
                endEffector = armService.GetState().EndEffector
            }, JsonOptions.Pretty)),
            "arm_set_joint" => SetJoint(args),
            "arm_set_joints" => SetJoints(args),
            "arm_home" => Home(),
            "arm_get_sensors" => TextResult(JsonSerializer.Serialize(armService.GetSensors(), JsonOptions.Pretty)),
            "arm_run_sequence" => RunSequence(args),
            _ => throw new McpDispatchException(-32601, $"Unknown tool: {name}")
        };
    }

    private object SetJoint(JsonElement args)
    {
        var id = args.GetProperty("joint_id").GetInt32();
        var angle = args.GetProperty("angle").GetDouble();

        if (!armService.SetJoint(id, angle))
        {
            throw new McpDispatchException(-32602, "joint_id must be 0-5");
        }

        var jointName = ArmConstants.JointDefs[id].Name;
        var current = armService.GetState().Joints[id];
        return TextResult($"Joint {id} ({jointName}) set to {current:F2} degrees");
    }

    private object SetJoints(JsonElement args)
    {
        var angles = args.GetProperty("angles")
            .EnumerateArray()
            .Select(x => x.GetDouble())
            .ToArray();

        if (!armService.SetJoints(angles))
        {
            throw new McpDispatchException(-32602, "angles must contain 6 numbers");
        }

        var text = string.Join(", ", armService.GetState().Joints.Select(a => a.ToString("F1")));
        return TextResult($"All joints set: {text}");
    }

    private object Home()
    {
        armService.Home();
        return TextResult("Arm moved to home position.");
    }

    private object RunSequence(JsonElement args)
    {
        var steps = args.GetProperty("steps")
            .EnumerateArray()
            .Select(step =>
            {
                var joints = step.GetProperty("joints").EnumerateArray().Select(x => x.GetDouble()).ToArray();
                var dwell = step.TryGetProperty("dwell_ms", out var dwellNode) ? dwellNode.GetInt32() : (int?)null;
                return new SequenceStep(joints, dwell);
            })
            .ToArray();

        armService.StartSequence(steps);
        return TextResult($"Sequence of {steps.Length} steps started.");
    }

    private static object TextResult(string text) => new
    {
        content = new[]
        {
            new { type = "text", text }
        }
    };

    private static string GetString(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            throw new McpDispatchException(-32602, $"Missing parameter: {key}");
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString()!;
        }

        throw new McpDispatchException(-32602, $"Invalid parameter type for: {key}");
    }

    private static JsonElement GetObject(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return default;
        }

        if (value is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            return element;
        }

        throw new McpDispatchException(-32602, $"Invalid parameter type for: {key}");
    }

    private static class JsonOptions
    {
        public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    }

    private sealed class McpDispatchException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
