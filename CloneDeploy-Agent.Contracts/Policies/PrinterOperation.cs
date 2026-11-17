namespace CloneDeploy_Agent.Contracts;

/// <summary>Shared printer operation validation.</summary>
public static class PrinterOperation
{
    public static string? Validate(string path, PrinterActionType operation, bool setDefault)
    {
        if (!Enum.IsDefined(operation)) return "Choose a valid printer operation.";
        var parts = path.StartsWith(@"\\", StringComparison.Ordinal) ? path[2..].Split('\\') : [];
        if (parts.Length != 2 || parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".."
                || part != part.Trim() || part.Any(c => char.IsControl(c) || "<>:\"/|?*".Contains(c))))
            return @"Enter a shared printer path in the form \\server\printer.";
        if (operation == PrinterActionType.Delete && setDefault)
            return "A removed printer cannot be set as the default.";
        return null;
    }
}
