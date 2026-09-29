namespace Mercury;

/// <summary>
/// Days a job may start. A missing list means every day, so older jobs are unchanged.
/// </summary>
public readonly record struct WeekSelection(
    bool Sunday,
    bool Monday,
    bool Tuesday,
    bool Wednesday,
    bool Thursday,
    bool Friday,
    bool Saturday)
{
    public static WeekSelection All { get; } = new(true, true, true, true, true, true, true);

    public static WeekSelection From(IReadOnlyList<DayOfWeek>? days)
    {
        if (days is null)
        {
            return All;
        }

        return new WeekSelection(
            days.Contains(DayOfWeek.Sunday),
            days.Contains(DayOfWeek.Monday),
            days.Contains(DayOfWeek.Tuesday),
            days.Contains(DayOfWeek.Wednesday),
            days.Contains(DayOfWeek.Thursday),
            days.Contains(DayOfWeek.Friday),
            days.Contains(DayOfWeek.Saturday));
    }

    /// <summary>Null when every day is selected so JSON stays absent for the default.</summary>
    public List<DayOfWeek>? ToStored()
    {
        var list = new List<DayOfWeek>(7);
        if (Sunday)
        {
            list.Add(DayOfWeek.Sunday);
        }

        if (Monday)
        {
            list.Add(DayOfWeek.Monday);
        }

        if (Tuesday)
        {
            list.Add(DayOfWeek.Tuesday);
        }

        if (Wednesday)
        {
            list.Add(DayOfWeek.Wednesday);
        }

        if (Thursday)
        {
            list.Add(DayOfWeek.Thursday);
        }

        if (Friday)
        {
            list.Add(DayOfWeek.Friday);
        }

        if (Saturday)
        {
            list.Add(DayOfWeek.Saturday);
        }

        return list.Count == 7 ? null : list;
    }
}

public static class ScheduleWeek
{
    private static readonly (DayOfWeek Day, string Short)[] DisplayOrder =
    [
        (DayOfWeek.Monday, "Mon"),
        (DayOfWeek.Tuesday, "Tue"),
        (DayOfWeek.Wednesday, "Wed"),
        (DayOfWeek.Thursday, "Thu"),
        (DayOfWeek.Friday, "Fri"),
        (DayOfWeek.Saturday, "Sat"),
        (DayOfWeek.Sunday, "Sun")
    ];

    public static bool Allows(JobOptions options, DayOfWeek day)
    {
        var days = options.ScheduleDays;
        if (days is null)
        {
            return true;
        }

        if (days.Count == 0)
        {
            return false;
        }

        if (days.Count >= 7 && DisplayOrder.All(pair => days.Contains(pair.Day)))
        {
            return true;
        }

        return days.Contains(day);
    }

    public static bool IsRestricted(JobOptions options) => options.ScheduleDays is not null
        && !DisplayOrder.All(pair => options.ScheduleDays.Contains(pair.Day));

    /// <summary>
    /// Shorter chip: "Schedule weekdays", "Schedule weekends", the days that are on, or the days that are off.
    /// Null when every day is allowed.
    /// </summary>
    public static string? Chip(JobOptions options)
    {
        if (!IsRestricted(options))
        {
            return null;
        }

        var on = DisplayOrder.Where(pair => options.ScheduleDays!.Contains(pair.Day)).ToList();
        if (on.Count == 0)
        {
            return "Schedule no days";
        }

        if (on.Count == 5
            && on.All(pair => pair.Day is >= DayOfWeek.Monday and <= DayOfWeek.Friday))
        {
            return "Schedule weekdays";
        }

        if (on.Count == 2
            && on.Any(pair => pair.Day == DayOfWeek.Saturday)
            && on.Any(pair => pair.Day == DayOfWeek.Sunday))
        {
            return "Schedule weekends";
        }

        var onLabel = "Schedule " + string.Join(' ', on.Select(pair => pair.Short));
        var off = DisplayOrder.Where(pair => !options.ScheduleDays!.Contains(pair.Day)).ToList();
        var offLabel = "Schedule except " + string.Join(' ', off.Select(pair => pair.Short));
        return onLabel.Length <= offLabel.Length ? onLabel : offLabel;
    }

    /// <summary>
    /// On a restricted week, the clock time of Schedule applies again each allowed day
    /// (in addition to the original date). Unrestricted jobs keep the one-shot date.
    /// </summary>
    public static bool ClockReached(Job job, DateTimeOffset now)
    {
        if (!IsRestricted(job.Options) || job.ScheduledStart is not { } start)
        {
            return true;
        }

        var clock = TimeOnly.FromDateTime(start.ToLocalTime().DateTime);
        return TimeOnly.FromDateTime(now.ToLocalTime().DateTime) >= clock;
    }
}
