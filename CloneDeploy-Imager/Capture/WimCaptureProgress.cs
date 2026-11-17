using System.Text;
using System.Text.RegularExpressions;

namespace CloneDeploy_Imager.Imaging;

// wimlib rewrites progress with CR, and pipe reads can split any part of a record.
/// <summary>
/// Parses fragmented wimlib output into monotonic capture progress without reporting premature completion.
/// </summary>
internal sealed partial class WimCaptureProgress
{
    private const int MaximumProgressLineCharacters = 4096;

    private readonly StringBuilder line = new();
    private bool truncated;
    private int percent = -1;
    public int Percent => Volatile.Read(ref percent);

    public void Append(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (character is '\r' or '\n') FinishLine();
            else if (line.Length < MaximumProgressLineCharacters) line.Append(character);
            else truncated = true;
        }
    }

    public void FinishLine()
    {
        if (!truncated)
        {
            var match = ArchivingPercent().Match(line.ToString());
            if (match.Success && int.TryParse(match.Groups[1].Value, out var value) && value <= 100)
            {
                // Tool progress can reach 100 while bytes still await server acknowledgement.
                value = Math.Min(value, 99);
                if (value > Percent) Volatile.Write(ref percent, value);
            }
        }
        line.Clear();
        truncated = false;
    }

    [GeneratedRegex(@"^Archiving file data: .*\(([0-9]{1,3})%\) done\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ArchivingPercent();
}
