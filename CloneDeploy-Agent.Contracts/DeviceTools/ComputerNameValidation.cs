namespace CloneDeploy_Agent.Contracts;

public static class ComputerNameValidation
{
    public static string? Error(string? name) => string.IsNullOrEmpty(name) || name.Length > 15 || name.All(char.IsAsciiDigit)
        || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || name.StartsWith('-') || name.EndsWith('-')
        ? "The device name must be 1–15 letters, digits or hyphens, must not be all numeric, and must start and end with a letter or digit."
        : null;
}
