using System.Text.Json;

namespace CloneDeploy_Agent.Contracts;

[Flags]
public enum PolicyWindowDays
{
    None = 0,
    Sunday = 1,
    Monday = 2,
    Tuesday = 4,
    Wednesday = 8,
    Thursday = 16,
    Friday = 32,
    Saturday = 64,
    EveryDay = 127
}

public sealed record PolicyStartWindow(PolicyWindowDays Days, TimeSpan From, TimeSpan Until)
{
    public void Validate()
    {
        if (Days == PolicyWindowDays.None || (Days & ~PolicyWindowDays.EveryDay) != 0)
            throw new InvalidDataException("Select at least one day for the allowed start window.");
        if (From < TimeSpan.Zero || From >= TimeSpan.FromDays(1)
            || Until < TimeSpan.Zero || Until >= TimeSpan.FromDays(1) || From == Until)
            throw new InvalidDataException("Choose different From and Until times within the same 24-hour day.");
    }

    public DateTime? OpeningDate(DateTime localTime)
    {
        Validate();
        var day = localTime.Date;
        if (Until > From)
        {
            if (localTime.TimeOfDay < From || localTime.TimeOfDay >= Until)
                return null;
        }
        else if (localTime.TimeOfDay < Until)
            day = day.AddDays(-1);
        else if (localTime.TimeOfDay < From)
            return null;

        return (Days & (PolicyWindowDays)(1 << (int)day.DayOfWeek)) != 0 ? day : null;
    }

    public static PolicyStartWindow? Read(JsonElement settings)
    {
        if (!settings.TryGetProperty("AllowedStartWindow", out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        var window = value.Deserialize<PolicyStartWindow>() ?? throw new InvalidDataException("Invalid allowed start window.");
        window.Validate();
        return window;
    }
}
