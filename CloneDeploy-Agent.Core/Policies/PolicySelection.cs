using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>
/// Reads individual policy settings and supplies defaults when a setting is missing.
/// </summary>
public static class PolicyValues
{
    public static int Number(this JsonElement value, string key, int fallback = 0) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Number
            ? item.GetInt32()
            : fallback;

    public static bool Flag(this JsonElement value, string key) =>
        value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.True;

    public static string Text(this JsonElement value, string key, string fallback = "") =>
        value.TryGetProperty( key, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()!
            : fallback;
}

/// <summary>
/// Checks received policies and decides which ones should run based on their schedule and history.
/// </summary>
public static class PolicySelection
{
    public static AgentPolicyContent Validate(AgentPolicySnapshot snapshot)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.Policy.GetRawText())));

        if (snapshot.SchemaVersion is not (1 or 2 or 3)
            || !hash.Equals(snapshot.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Policy snapshot version or SHA-256 is invalid.");

        var content = snapshot.Policy.Deserialize<AgentPolicyContent>()
                      ?? throw new InvalidDataException("Missing policy.");

        if (content.Id <= 0 || content.Guid == Guid.Empty
            || content.Actions.Select(x => x.Guid).Distinct().Count() != content.Actions.Count)
            throw new InvalidDataException("Invalid policy identity or duplicate actions.");

        _ = PolicyUserContext.UsesUserContext(content.Settings);

        if (snapshot.SchemaVersion < PolicyUserContext.SchemaVersion(content.Settings))
            throw new InvalidDataException("The policy snapshot schema does not support its configured features.");

        return content;
    }

    public static bool IsDue(
        AgentPolicySnapshot snapshot,
        AgentPolicyContent policy,
        int trigger,
        PolicyHistoryState history,
        DateTime utcNow)
    {
        var settings = policy.Settings;
        var configuredTrigger = settings.Number("Trigger");
        // Self-service-only policies are admitted through the explicit Run path, never scheduling.
        if (configuredTrigger == (int)PolicyTrigger.SelfServiceOnly)
            return false;

        if (configuredTrigger != trigger
            && !(configuredTrigger == (int)PolicyTrigger.StartupOrCheckin
                 && trigger is (int)PolicyTrigger.Startup or (int)PolicyTrigger.Checkin))
            return false;

        if (DateTime.TryParse(
                settings.Text("StartDateUtc"),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var start)
            && start.ToUniversalTime() > utcNow)
            return false;

        var localNow = utcNow.ToLocalTime();
        var window = PolicyStartWindow.Read(settings);
        var openingDate = window?.OpeningDate(localNow);
        if (window is not null ? openingDate is null : !WithinWindow(policy.StartWindow, policy.EndWindow, localNow))
            return false;
        // Interrupted execution may have made changes. Never automatically retry that snapshot.
        if (history.InterruptedHash)
            return false;

        var last = history.LastCompletedAtUtc;
        var frequency = settings.Number("Frequency");

        if (frequency is (int)PolicyFrequency.OncePerComputer or (int)PolicyFrequency.OncePerUserPerComputer)
            return last is null || (snapshot.ReRunExisting && !history.CompletedHash);

        var localLast = last?.ToLocalTime();
        var scheduleDay = openingDate ?? localNow.Date;
        var lastStart = (history.LastStartedAtUtc ?? last)?.ToLocalTime();
        var lastWindowDay = lastStart.HasValue ? window?.OpeningDate(lastStart.Value) ?? lastStart.Value.Date : (DateTime?)null;
        var sub = settings.Number("SubFrequency");
        var dayOnly = settings.Number("MissedAction") == (int)PolicyFrequencyMissedAction.ScheduleDayOnly;

        return frequency switch
        {
            (int)PolicyFrequency.Ongoing => true,
            (int)PolicyFrequency.OncePerDay => window is null
                ? localLast is null || localLast.Value.Date < localNow.Date
                : lastWindowDay is null || lastWindowDay < scheduleDay,
            (int)PolicyFrequency.OncePerWeek => (!dayOnly || (int)scheduleDay.DayOfWeek == sub)
                                                &&
                                                (localLast is null || localLast < LatestWeekday(scheduleDay, sub)),
            (int)PolicyFrequency.OncePerMonth => (!dayOnly
                                                  || scheduleDay.Day == Math.Min(
                                                      sub,
                                                      DateTime.DaysInMonth(scheduleDay.Year, scheduleDay.Month)))
                                                 &&
                                                 (localLast is null || localLast < LatestMonthDay(scheduleDay, sub)),
            (int)PolicyFrequency.EveryXhours => sub > 0 && (last is null || utcNow - last >= TimeSpan.FromHours(sub)),
            (int)PolicyFrequency.EveryXdays => sub > 0 && (last is null || utcNow - last >= TimeSpan.FromDays(sub)),
            _ => throw new InvalidDataException("Unsupported policy frequency.")
        };
    }

    private static DateTime LatestWeekday(DateTime now, int day) => now.Date.AddDays(-(((int)now.DayOfWeek - day + 7) % 7));

    private static DateTime LatestMonthDay(DateTime now, int day)
    {
        if (day is < 1 or > 31)
            throw new InvalidDataException("Invalid monthly day.");

        var date = new DateTime(now.Year, now.Month, Math.Min(day, DateTime.DaysInMonth(now.Year, now.Month)));

        if (date <= now)
            return date;

        var previous = now.AddMonths(-1);

        return new DateTime(
            previous.Year,
            previous.Month,
            Math.Min(day, DateTime.DaysInMonth(previous.Year, previous.Month)));
    }

    public static bool WithinWindow(JsonElement? start, JsonElement? end, DateTime localNow)
    {
        if (start is null && end is null)
            return true;

        static DateTime? Latest(JsonElement? schedule, DateTime now)
        {
            if (schedule is null)
                return null;

            if (!TimeSpan.TryParse(schedule.Value.Text("Time"), out var time))
                throw new InvalidDataException("Invalid policy window time.");

            for (var i = 0; i <= 7; i++)
            {
                var date = now.Date.AddDays(-i);

                if (schedule.Value.Flag(date.DayOfWeek.ToString()) && date + time <= now)
                    return date + time;
            }

            return null;
        }

        // A single boundary is a same-day lower/upper bound. Paired boundaries support overnight windows.
        if (end is null)
            return start!.Value.Flag(localNow.DayOfWeek.ToString()) && Latest(start, localNow)?.Date == localNow.Date;

        if (start is null)
            return end.Value.Flag(localNow.DayOfWeek.ToString())
                   && localNow.TimeOfDay < TimeSpan.Parse(end.Value.Text("Time"));

        var opened = Latest(start, localNow);
        var closed = Latest(end, localNow);

        return opened is not null && (closed is null || opened > closed);
    }
}
