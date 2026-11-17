namespace CloneDeploy_Server.WinPe;

public static class WinPeKeyboardLayouts
{
    public const string Default = "0409:00000409";
    public sealed record Layout(string Id, string Name);
    // Standard keyboard layouts; IMEs requiring additional language packages are excluded.
    public static IReadOnlyList<Layout> All { get; } = Array.AsReadOnly(new Layout[]
    {
        new(Default, "English (US)"),
        new("0809:00000809", "English (United Kingdom)"),
        new("0409:00020409", "US International"),
        new("0409:00010409", "US Dvorak"),
        new("040C:0000040C", "French"),
        new("0C0C:00001009", "Canadian French"),
        new("080C:0000080C", "Belgian French"),
        new("100C:0000100C", "Swiss French"),
        new("0407:00000407", "German"),
        new("0807:00000807", "Swiss German"),
        new("0413:00000413", "Dutch"),
        new("0410:00000410", "Italian"),
        new("040A:0000040A", "Spanish"),
        new("080A:0000080A", "Latin American"),
        new("0816:00000816", "Portuguese"),
        new("0416:00010416", "Portuguese (Brazil ABNT2)"),
        new("0406:00000406", "Danish"),
        new("0414:00000414", "Norwegian"),
        new("041D:0000041D", "Swedish"),
        new("040B:0000040B", "Finnish"),
        new("0415:00000415", "Polish (Programmers)"),
        new("0405:00000405", "Czech"),
        new("041B:0000041B", "Slovak"),
        new("040E:0000040E", "Hungarian"),
        new("041F:0000041F", "Turkish Q")
    });

    public static bool IsSupported(string? id) => All.Any(x => x.Id == id);
}
