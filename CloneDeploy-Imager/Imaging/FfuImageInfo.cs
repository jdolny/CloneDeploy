using System.Globalization;
using System.Text.RegularExpressions;

namespace CloneDeploy_Imager.Imaging;

internal static class FfuImageInfo
{
    // DISM /English /Get-ImageInfo: Size is image size, not target disk capacity.
    // Require one store's sector metadata rather than guessing from the file length.
    public static long MinimumDiskSizeBytes(string output)
    {
        output = output.Replace("\r\n", "\n").Replace('\r', '\n');
        var sectorSize = Read("Sector Size", @"[ \t]+bytes");
        var sectorCount = Read("Minimum Sector Count", "");
        try { return checked(sectorSize * sectorCount); }
        catch (OverflowException ex) { throw new InvalidDataException("FFU minimum disk size exceeds the supported range.", ex); }

        long Read(string label, string suffix)
        {
            var matches = Regex.Matches(output,
                @"^[ \t]*" + label + @"[ \t]*:[ \t]*(?<value>[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)" + suffix + @"[ \t]*\r?$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (matches.Count != 1 || !long.TryParse(matches[0].Groups["value"].Value,
                    NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value) || value <= 0)
                throw new InvalidDataException($"DISM did not report a valid, single FFU {label}. Minimum disk size could not be determined.");
            return value;
        }
    }
}
