using System.Text;

namespace CloneDeploy_Agent.Contracts;

public enum WinGetScope { Default = 0, Machine = 1, User = 2 }

public static class WinGetScopeOptions
{
    public static WinGetScope Resolve(WinGetScope selected, string? arguments, string? additionalArguments)
    {
        if (!Enum.IsDefined(selected)) throw new InvalidDataException("Choose a valid WinGet scope.");
        WinGetScope? custom = null;
        var tokens = Tokens((arguments ?? "") + " " + (additionalArguments ?? ""));
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Equals("--override", StringComparison.OrdinalIgnoreCase) || token.Equals("--custom", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            string? value = null;
            if (token.Equals("--scope", StringComparison.OrdinalIgnoreCase))
                value = ++i < tokens.Count ? tokens[i] : "";
            else if (token.StartsWith("--scope=", StringComparison.OrdinalIgnoreCase)) value = token[8..];
            if (value is null) continue;
            if (selected != WinGetScope.Default || custom.HasValue)
                throw new InvalidDataException("Set WinGet scope in either the Scope dropdown or arguments, and specify it only once.");
            custom = value.ToLowerInvariant() switch
            {
                "user" => WinGetScope.User, "machine" => WinGetScope.Machine,
                _ => throw new InvalidDataException("WinGet --scope must be user or machine.")
            };
        }
        return custom ?? selected;
    }

    // Windows argument quoting, including escaped quotes and backslashes before quotes.
    private static List<string> Tokens(string input)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (!char.IsWhiteSpace(c) || quoted) started = true;
            if (c == '\\')
            {
                var start = i;
                while (i < input.Length && input[i] == '\\') i++;
                var count = i - start;
                if (i < input.Length && input[i] == '"')
                {
                    token.Append('\\', count / 2);
                    if (count % 2 == 1) token.Append('"'); else quoted = !quoted;
                }
                else { token.Append('\\', count); i--; }
            }
            else if (c == '"') quoted = !quoted;
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started) { result.Add(token.ToString()); token.Clear(); started = false; }
            }
            else token.Append(c);
        }
        if (quoted) throw new InvalidDataException("WinGet arguments contain an unmatched quote.");
        if (started) result.Add(token.ToString());
        return result;
    }
    public static void ValidateContext(WinGetScope scope, bool isSystem)
    {
        if (scope == WinGetScope.User && isSystem)
            throw new InvalidDataException("User scope requires running as a user. Configure the policy to run in user context or choose another scope.");
    }
}
