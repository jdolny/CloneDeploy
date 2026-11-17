namespace CloneDeploy_Agent.Contracts;

public enum AgentTerminalShell { PowerShell, CommandPrompt, Zsh }

public sealed record AgentTerminalRequest(Guid SessionId, Guid DeviceGuid, DateTimeOffset ExpiresAtUtc,
    AgentTerminalShell Shell);

public sealed record AgentTerminalReply(Guid SessionId, bool Accepted, string Detail);

public static class AgentTerminalLimits
{
    public const int MaximumInputBytes = 4096;
    public const int MinimumColumns = 20;
    public const int MaximumColumns = 400;
    public const int MinimumRows = 5;
    public const int MaximumRows = 200;

    public const int MaximumMessageBytes = 16 * 1024;
    public const int OutputChunkBytes = 8192;
    public const int OutputWindow = 8;
    public static bool ValidSize(int columns, int rows) => columns is >= MinimumColumns and <= MaximumColumns && rows is >= MinimumRows and <= MaximumRows;
}
