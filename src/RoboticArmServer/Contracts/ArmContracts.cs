namespace RoboticArmServer.Contracts;

public static class ArmConstants
{
    public static readonly JointDef[] JointDefs =
    [
        new(0, "Base", "y", -180, 180),
        new(1, "Shoulder", "z", -120, 80),
        new(2, "Elbow", "z", -135, 135),
        new(3, "WristPitch", "z", -90, 90),
        new(4, "WristRoll", "y", -180, 180),
        new(5, "Gripper", "z", 0, 80)
    ];

    public static readonly double[] HomePosition = [0, 40, -90, 50, 0, 20];

    public static readonly HashSet<string> AllowedModes = ["manual", "auto", "hold"];
}

public sealed record JointDef(int Id, string Name, string Axis, double Min, double Max);

public sealed record ArmStateResponse(
    double[] Joints,
    JointDef[] JointDefs,
    string Mode,
    bool IsMoving,
    EndEffector EndEffector,
    long Timestamp);

public sealed record EndEffector(
    double X,
    double Y,
    double Z,
    double YawDeg,
    double PitchDeg,
    double RollDeg);

public sealed record SensorJoint(
    int Id,
    string Name,
    double AngleDeg,
    double TorqueNm,
    double VelocityRads,
    double TempC);

public sealed record GripperData(double ApertureDeg, double ForceN, bool Contact);

public sealed record PowerData(double VoltageV, double CurrentA);

public sealed record SensorsResponse(
    SensorJoint[] Joints,
    EndEffector EndEffector,
    GripperData Gripper,
    PowerData Power,
    long Timestamp);

public sealed record ArmBroadcastData(double[] Joints, string Mode, bool IsMoving, long Timestamp);

public sealed record WelcomeData(double[] Joints, string Mode);

public sealed record ApiResult(bool Ok, string? Message = null);

public sealed record SetJointResult(bool Ok, int Joint, double Angle, string? Message = null);

public sealed record SetJointsResult(bool Ok, double[] Joints, string? Message = null);

public sealed record ModeResult(bool Ok, string Mode, string? Message = null);

public sealed record SequenceAcceptedResult(bool Ok, int Steps, string Message);

public sealed record ErrorResult(string Error);

public sealed record SetJointRequest(double Angle);

public sealed record SetJointsRequest(double[] Angles);

public sealed record SetModeRequest(string Mode);

public sealed record SequenceStep(double[] Joints, int? DwellMs);

public sealed record SequenceRequest(SequenceStep[] Steps);
