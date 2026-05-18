using RoboticArmServer.Contracts;

namespace RoboticArmServer.Services;

public sealed class ArmSimulatorService
{
    private readonly Lock _sync = new();

    private double[] _joints = ArmConstants.HomePosition.ToArray();
    private string _mode = "manual";
    private bool _isMoving;
    private long _timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public event Func<string, Task>? StateChanged;

    public ArmStateResponse GetState()
    {
        lock (_sync)
        {
            return new ArmStateResponse(
                _joints.ToArray(),
                ArmConstants.JointDefs,
                _mode,
                _isMoving,
                ForwardKinematics(_joints),
                _timestamp);
        }
    }

    public SensorsResponse GetSensors()
    {
        lock (_sync)
        {
            var joints = _joints
                .Select((angle, i) => new SensorJoint(
                    i,
                    ArmConstants.JointDefs[i].Name,
                    Round(angle + Noise(0.02), 4),
                    Round(Math.Abs(angle) * 0.012 + Noise(0.05) + 0.03, 4),
                    Round(Noise(0.008), 6),
                    Round(28 + Math.Abs(angle) * 0.04 + Noise(0.2), 2)))
                .ToArray();

            var current = _joints.Sum(a => Math.Abs(a) * 0.001) + 0.2 + Noise(0.02);
            var gripperAperture = _joints[5];

            return new SensorsResponse(
                joints,
                ForwardKinematics(_joints),
                new GripperData(gripperAperture, Round(Noise(0.1) + 0.05, 4), gripperAperture < 5),
                new PowerData(Round(24.0 + Noise(0.1), 3), Round(current, 3)),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    public JointDef[] GetJointDefs() => ArmConstants.JointDefs;

    public bool SetJoint(int id, double angle)
    {
        if (id < 0 || id > 5)
        {
            return false;
        }

        lock (_sync)
        {
            _joints[id] = ClampAngle(angle, id);
            Touch();
        }

        PublishState();
        return true;
    }

    public bool SetJoints(IReadOnlyList<double> angles)
    {
        if (angles.Count != 6)
        {
            return false;
        }

        lock (_sync)
        {
            _joints = angles.Select((a, i) => ClampAngle(a, i)).ToArray();
            Touch();
        }

        PublishState();
        return true;
    }

    public void Home() => SetJoints(ArmConstants.HomePosition);

    public void Zero() => SetJoints([0, 0, 0, 0, 0, 0]);

    public bool SetMode(string mode)
    {
        if (!ArmConstants.AllowedModes.Contains(mode))
        {
            return false;
        }

        lock (_sync)
        {
            _mode = mode;
            Touch();
        }

        PublishState();
        return true;
    }

    public void StartSequence(IReadOnlyList<SequenceStep> steps)
    {
        _ = Task.Run(async () =>
        {
            lock (_sync)
            {
                _isMoving = true;
                Touch();
            }

            PublishState();

            foreach (var step in steps)
            {
                SetJoints(step.Joints);
                await Task.Delay(step.DwellMs ?? 500);
            }

            lock (_sync)
            {
                _isMoving = false;
                Touch();
            }

            PublishState("sequence_complete");
        });
    }

    public ArmBroadcastData GetBroadcastData()
    {
        lock (_sync)
        {
            return new ArmBroadcastData(_joints.ToArray(), _mode, _isMoving, _timestamp);
        }
    }

    public WelcomeData GetWelcomeData()
    {
        lock (_sync)
        {
            return new WelcomeData(_joints.ToArray(), _mode);
        }
    }

    private static double ClampAngle(double value, int jointId)
    {
        var def = ArmConstants.JointDefs[jointId];
        return Math.Max(def.Min, Math.Min(def.Max, value));
    }

    private static EndEffector ForwardKinematics(IReadOnlyList<double> angles)
    {
        var r = DegreeToRadian(angles[0]);
        var t1 = DegreeToRadian(angles[1]);
        var t2 = t1 + DegreeToRadian(angles[2]);
        var t3 = t2 + DegreeToRadian(angles[3]);

        const double l1 = 1.1;
        const double l2 = 0.9;
        const double l3 = 0.52;

        var reach = l1 * Math.Cos(t1) + l2 * Math.Cos(t2) + l3 * Math.Cos(t3);

        return new EndEffector(
            Round(reach * Math.Sin(r), 4),
            Round(0.82 + l1 * Math.Sin(t1) + l2 * Math.Sin(t2) + l3 * Math.Sin(t3), 4),
            Round(reach * Math.Cos(r), 4),
            Round(angles[0], 2),
            Round(angles[1] + angles[2] + angles[3], 2),
            Round(angles[4], 2));
    }

    private static double DegreeToRadian(double angle) => angle * (Math.PI / 180.0);

    private static double Noise(double scale) => (Random.Shared.NextDouble() - 0.5) * 2 * scale;

    private static double Round(double value, int digits) => Math.Round(value, digits, MidpointRounding.AwayFromZero);

    private void Touch() => _timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void PublishState(string type = "state")
    {
        var handler = StateChanged;
        if (handler is not null)
        {
            _ = Task.Run(() => handler(type));
        }
    }
}
