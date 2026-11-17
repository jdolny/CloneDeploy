using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Xml.Linq;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Reads retained update events and the Automatic Updates success timestamp without starting an update scan.</summary>
public static class WindowsUpdateActivityCollector
{
    private static readonly TimeSpan EventReadTimeout = TimeSpan.FromSeconds(2);

    private const string Provider = "Microsoft-Windows-WindowsUpdateClient";

    public static InventoryWindowsUpdateActivity Collect(ILogger logger)
    {
        var result = new InventoryWindowsUpdateActivity { CollectedAtUtc = DateTime.UtcNow };

        foreach (var log in new[] { $"{Provider}/Operational", "System" })
        {
            try
            {
                using var configuration = new EventLogConfiguration(log);

                if (!configuration.IsEnabled)
                {
                    result.UnavailableSources.Add($"{log} (disabled)");
                    continue;
                }

                ReadLatest(
                    "EventID=19",
                    entry =>
                    {
                        result.LastInstallationSuccessAtUtc = Latest(
                            result.LastInstallationSuccessAtUtc,
                            entry.TimeCreated?.ToUniversalTime());
                    });
                ReadLatest(
                    "EventID=41",
                    entry =>
                    {
                        result.LastDownloadSuccessAtUtc = Latest(
                            result.LastDownloadSuccessAtUtc,
                            entry.TimeCreated?.ToUniversalTime());
                    });
                ReadLatest(
                    "EventID=20 or EventID=31",
                    entry =>
                    {
                        if (entry.TimeCreated is not { } time)
                            return;

                        var utc = time.ToUniversalTime();

                        if (result.LastFailureAtUtc is not null && utc <= result.LastFailureAtUtc)
                            return;

                        result.LastFailureAtUtc = utc;
                        result.LastFailureOperation = entry.Id == 20 ? "Installation" : "Download";
                        result.LastFailureErrorCode = ReadErrorCode(entry.ToXml());
                    });

                void ReadLatest(string filter, Action<EventRecord> read)
                {
                    var query = $"*[System[Provider[@Name='{Provider}'] and ({filter})]]";
                    using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, query)
                    {
                        ReverseDirection = true
                    });
                    using var entry = reader.ReadEvent(EventReadTimeout);

                    if (entry is not null)
                        read(entry);
                }
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or SecurityException or System.Xml.XmlException)
            {
                result.UnavailableSources.Add(log);
                logger.LogDebug(ex, $"Windows Update activity source unavailable: {log}");
            }
        }

        try
        {
            dynamic automatic = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate", true)!)!;

            try
            {
                dynamic results = automatic.Results;

                try
                {
                    if (results.LastInstallationSuccessDate is DateTime date && date.Year > 1900)
                        result.LastAutomaticInstallationSuccessAtUtc = DateTime.SpecifyKind(date, DateTimeKind.Utc);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(results);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(automatic);
            }
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            result.UnavailableSources.Add("Automatic Updates installation timestamp");
            logger.LogDebug(ex, "Automatic Updates installation timestamp unavailable");
        }

        return result;
    }

    private static DateTime? Latest(DateTime? first, DateTime? second) =>
        first is null ? second : second is null || first >= second ? first : second;

    public static string? ReadErrorCode(string xml)
    {
        var error = XDocument.Parse(xml).Descendants()
            .FirstOrDefault(x => x.Name.LocalName == "Data"
                                 &&
                                 string.Equals(x.Attribute("Name")?.Value, "errorCode", StringComparison.OrdinalIgnoreCase))?.Value;

        if (error is null)
            return null;

        if (error.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            &&
            uint.TryParse(error[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return $"0x{hex:X8}";

        if (long.TryParse(error, CultureInfo.InvariantCulture, out var number)
            && number is >= int.MinValue and <= uint.MaxValue)
            return $"0x{unchecked((uint)number):X8}";

        return error.Length > 128 ? error[..128] : error;
    }

    // Named event fields handle both older templates (GUID only) and newer templates that include a title.
}