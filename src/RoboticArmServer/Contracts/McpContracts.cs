namespace RoboticArmServer.Contracts;

public sealed record McpRpcRequest(string Jsonrpc, object? Id, string Method, Dictionary<string, object?>? Params);

public sealed record McpRpcResponse(object? Id, object? Result = null, McpRpcError? Error = null)
{
    public string Jsonrpc { get; init; } = "2.0";
}

public sealed record McpRpcError(int Code, string Message);

public sealed record McpTool(string Name, string Description, object InputSchema);
