using System.Text;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>
/// Turns an action result into a short log entry, including captured output when requested.
/// </summary>
public static class ActionExecutionLog
{
    private const int MaximumActionNameLength = 200;
    private const int MaximumOutputLength = 1700;

    public static string Format(
        AgentPolicyAction action,
        PolicyProcessResult result,
        bool includeOutput = true)
    {
        var text = new StringBuilder($"{action.Name[..Math.Min(action.Name.Length, MaximumActionNameLength)]}: exit {result.ExitCode}");

        void Append(string label, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            text.AppendLine().Append(label).AppendLine(":");
            text.Append(value.Length > MaximumOutputLength ? $"{value[..MaximumOutputLength]}\n[truncated]" : value);
        }

        if (includeOutput)
        {
            // WinGet always captures diagnostics, including failures before installation starts.
            var winGet = action.Type == (int)ActionType.Software
                         && action.Settings.Number("Type") == (int)SoftwareActionType.WinGet
                         && !action.Settings.Flag("Uninstall");

            if (winGet || action.Settings.Flag("RedirectStdOut"))
                Append("stdout", result.Output);

            if (winGet || action.Settings.Flag("RedirectStdError"))
                Append("stderr", result.Error);
        }

        return text.ToString();
    }
}