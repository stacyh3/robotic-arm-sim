using RoboticArmServer.Contracts;
using RoboticArmServer.Services;

namespace RoboticArmServer.Api;

public static class ArmApiModule
{
    public static IEndpointRouteBuilder MapArmApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/arm");

        group.MapGet("/state", (ArmSimulatorService arm) => Results.Ok(arm.GetState()));

        group.MapGet("/sensors", (ArmSimulatorService arm) => Results.Ok(arm.GetSensors()));

        group.MapGet("/joints/defs", (ArmSimulatorService arm) => Results.Ok(arm.GetJointDefs()));

        group.MapPost("/joint/{id:int}", (int id, SetJointRequest req, ArmSimulatorService arm) =>
        {
            if (id is < 0 or > 5)
            {
                return Results.BadRequest(new ErrorResult("Joint id must be 0-5"));
            }

            if (!arm.SetJoint(id, req.Angle))
            {
                return Results.BadRequest(new ErrorResult("Joint id must be 0-5"));
            }

            return Results.Ok(new SetJointResult(true, id, arm.GetState().Joints[id]));
        });

        group.MapPost("/joints", (SetJointsRequest req, ArmSimulatorService arm) =>
        {
            if (req.Angles is null || req.Angles.Length != 6)
            {
                return Results.BadRequest(new ErrorResult("angles must be array of 6 numbers"));
            }

            arm.SetJoints(req.Angles);
            return Results.Ok(new SetJointsResult(true, arm.GetState().Joints));
        });

        group.MapPost("/home", (ArmSimulatorService arm) =>
        {
            arm.Home();
            return Results.Ok(new SetJointsResult(true, arm.GetState().Joints));
        });

        group.MapPost("/zero", (ArmSimulatorService arm) =>
        {
            arm.Zero();
            return Results.Ok(new SetJointsResult(true, arm.GetState().Joints));
        });

        group.MapPost("/mode", (SetModeRequest req, ArmSimulatorService arm) =>
        {
            if (string.IsNullOrWhiteSpace(req.Mode) || !arm.SetMode(req.Mode))
            {
                return Results.BadRequest(new ErrorResult("mode must be auto | manual | hold"));
            }

            return Results.Ok(new ModeResult(true, req.Mode));
        });

        group.MapPost("/sequence", (SequenceRequest req, ArmSimulatorService arm) =>
        {
            if (req.Steps is null)
            {
                return Results.BadRequest(new ErrorResult("steps[] required"));
            }

            if (req.Steps.Any(step => step.Joints is null || step.Joints.Length != 6))
            {
                return Results.BadRequest(new ErrorResult("each step must include joints array of 6 numbers"));
            }

            arm.StartSequence(req.Steps);
            return Results.Ok(new SequenceAcceptedResult(true, req.Steps.Length, "Sequence started"));
        });

        return app;
    }
}
