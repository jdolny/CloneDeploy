using CloneDeploy_Server.Backend.Data.Models;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>
/// Checks schedule settings and calculates the next time they should run.
/// </summary>
public sealed class ScheduleCalculator
{
    public bool IsValid(Schedule schedule) => schedule.Type switch
    {
        ScheduleType.Interval => schedule.IntervalMinutes is >= 5 &&
                                 schedule.StartsAtUtc is { Kind: DateTimeKind.Utc },
        ScheduleType.Weekly => schedule.Time is { } time && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1) &&
                               time.Ticks % TimeSpan.FromMinutes(15).Ticks == 0 &&
                               (schedule.Monday || schedule.Tuesday || schedule.Wednesday || schedule.Thursday ||
                                schedule.Friday || schedule.Saturday || schedule.Sunday),
        _ => false
    };

    private static TimeZoneInfo TimeZone => TimeZoneInfo.Local;

    // Return the next occurrence according to the supplied UTC time.
    public DateTime? GetNextScheduledTime(Schedule schedule, DateTime afterUtc)
    {
        if (!IsValid(schedule)) return null;

        if (schedule.Type == ScheduleType.Interval)
        {
            var start = schedule.StartsAtUtc!.Value;
            if (afterUtc < start) return start;

            // Fixed elapsed intervals from the anchor, independent of run duration or DST.
            var intervalTicks = TimeSpan.FromMinutes(schedule.IntervalMinutes!.Value).Ticks;
            var remainingTicks = intervalTicks - (afterUtc.Ticks - start.Ticks) % intervalTicks;
            return remainingTicks > DateTime.MaxValue.Ticks - afterUtc.Ticks
                ? null
                : new DateTime(afterUtc.Ticks + remainingTicks, DateTimeKind.Utc);
        }

        var searchStartDate = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, TimeZone).Date;

        for (var day = 0; day < 15; day++)
        {
            var scheduledDate = searchStartDate.AddDays(day);

            if (!RunsOn(schedule, scheduledDate.DayOfWeek)) continue;

            var scheduledLocalTime = DateTime.SpecifyKind(scheduledDate + schedule.Time!.Value, DateTimeKind.Unspecified);

            // A spring clock change can skip this time. Wait for the next scheduled day.
            if (TimeZone.IsInvalidTime(scheduledLocalTime)) continue;

            DateTime scheduledUtcTime;

            if (TimeZone.IsAmbiguousTime(scheduledLocalTime))
            {
                // During the fall clock change, use the first occurrence only.
                scheduledUtcTime = new DateTimeOffset(
                    scheduledLocalTime,
                    TimeZone.GetAmbiguousTimeOffsets(scheduledLocalTime).Max()).UtcDateTime;
            }
            else
            {
                scheduledUtcTime = TimeZoneInfo.ConvertTimeToUtc(scheduledLocalTime, TimeZone);
            }

            if (scheduledUtcTime > afterUtc)
                return scheduledUtcTime;
        }

        return null;
    }

    private static bool RunsOn(Schedule schedule, DayOfWeek day)
    {
        return day switch
        {
            DayOfWeek.Monday => schedule.Monday,
            DayOfWeek.Tuesday => schedule.Tuesday,
            DayOfWeek.Wednesday => schedule.Wednesday,
            DayOfWeek.Thursday => schedule.Thursday,
            DayOfWeek.Friday => schedule.Friday,
            DayOfWeek.Saturday => schedule.Saturday,
            DayOfWeek.Sunday => schedule.Sunday,
            _ => false
        };
    }
}