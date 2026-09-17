namespace Mercury;

public static class JobDue
{
    public static bool IsDue(Job job, DateTimeOffset now)
    {
        if (job.ScheduledStart is { } scheduled && now < scheduled)
        {
            return false;
        }

        if (!job.Options.HoursEnabled)
        {
            return true;
        }

        var local = now.ToLocalTime().DateTime;
        return RunWindow.IsInside(TimeOnly.FromDateTime(local), job.Options.HoursStart, job.Options.HoursEnd);
    }

    /// <summary>
    /// Next auto-start: first not-on-hold Pending in list order. A not-due head blocks later jobs.
    /// <paramref name="forceJobId"/> (Resume/Start that row) starts that Pending job even if it is not first or not due.
    /// </summary>
    public static Job? FindNext(IEnumerable<Job> jobs, DateTimeOffset now, string? forceJobId = null)
    {
        var list = jobs as IList<Job> ?? jobs.ToList();
        if (!string.IsNullOrEmpty(forceJobId))
        {
            var forced = list.FirstOrDefault(j =>
                j.Id == forceJobId && !j.OnHold && j.Status == JobStatus.Pending);
            if (forced is not null)
            {
                return forced;
            }
        }

        foreach (var job in list)
        {
            if (job.OnHold)
            {
                continue;
            }

            if (job.Status != JobStatus.Pending)
            {
                continue;
            }

            return IsDue(job, now) ? job : null;
        }

        return null;
    }

    public static string StatusLabel(Job job, DateTimeOffset now)
    {
        if (job.OnHold)
        {
            return "On hold";
        }

        if (job.Status != JobStatus.Pending)
        {
            return job.Status switch
            {
                JobStatus.PausedOutsideHours => "Waiting for hours",
                JobStatus.Paused => "Paused",
                JobStatus.Preparing => "Preparing destination",
                JobStatus.Enumerating => "Enumerating",
                JobStatus.Copying => "Copying",
                JobStatus.Verifying => "Verifying",
                JobStatus.Completed => "Completed",
                JobStatus.Incomplete => "Incomplete",
                JobStatus.Cancelled => "Stopped",
                JobStatus.Failed => "Failed",
                _ => job.Status.ToString()
            };
        }

        if (job.ScheduledStart is { } scheduled && now < scheduled)
        {
            return $"Starts {scheduled.LocalDateTime:ddd d MMM HH:mm}";
        }

        if (job.Options.HoursEnabled &&
            !RunWindow.IsInside(TimeOnly.FromDateTime(now.ToLocalTime().DateTime), job.Options.HoursStart, job.Options.HoursEnd))
        {
            return $"Waiting for hours ({job.Options.HoursStart:HH:mm}–{job.Options.HoursEnd:HH:mm})";
        }

        return "Queued";
    }
}
