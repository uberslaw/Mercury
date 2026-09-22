namespace Mercury;

/// <summary>
/// Compact, non-default flags for a queue tile. Defaults (unlimited speed, Quick verify,
/// retries 3 / wait 5, skip-if-newer, RoboFlags defaults) stay off the chip row.
/// </summary>
public static class JobOptionBadges
{
    public static IReadOnlyList<string> For(Job job)
    {
        var o = job.Options;
        var badges = new List<string>();

        if (job.OnHold)
        {
            badges.Add("Hold");
        }

        if (job.Catcher is { } catcher)
        {
            badges.Add(string.IsNullOrWhiteSpace(catcher.TemplateName) ? "Catcher" : "Catcher " + catcher.TemplateName);
        }

        if (o.DryRun)
        {
            badges.Add("Dry Run");
        }

        if (o.PackAsZip)
        {
            badges.Add("Small Files");
            if (!o.SkipCompressedWhenPacking)
            {
                badges.Add("Wrap compressed");
            }
        }

        if (o.IgnoreFreeSpaceCheck)
        {
            badges.Add("Ignore Storage Limit");
        }

        if (o.HoursEnabled)
        {
            badges.Add($"Window {o.HoursStart:HH:mm}–{o.HoursEnd:HH:mm}");
        }

        if (o.Verify == VerifyLevel.Thorough)
        {
            badges.Add("Verify Thorough");
        }

        if (o.MaxMegabytesPerSecond is > 0)
        {
            badges.Add($"{o.MaxMegabytesPerSecond.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} MB/s");
        }

        if (job.ScheduledStart is { } start)
        {
            badges.Add($"Start After {start.LocalDateTime:ddd d MMM HH:mm}");
        }

        badges.AddRange(o.Overwrite switch
        {
            OverwritePolicy.Always => ["Always overwrite"],
            OverwritePolicy.NeverIfExists => ["Never overwrite"],
            _ => Array.Empty<string>()
        });

        if (o.RetryCount != 3)
        {
            badges.Add($"Retries {o.RetryCount}");
        }

        if (o.RetryWaitSeconds != 5)
        {
            badges.Add($"Wait {o.RetryWaitSeconds}s");
        }

        if (!o.CopyTimestamps)
        {
            badges.Add("timestamps off");
        }

        if (!o.CopyAttributes)
        {
            badges.Add("attributes off");
        }

        if (o.CopySecurity)
        {
            badges.Add("ACL");
        }

        if (o.CopyOwner)
        {
            badges.Add("owner");
        }

        if (!o.CopyDirectoryTimestamps)
        {
            badges.Add("dir times off");
        }

        if (!o.CopyEmptyDirectories)
        {
            badges.Add("skip empty dirs");
        }

        if (o.UnbufferedIo)
        {
            badges.Add("force unbuffered");
        }

        if (o.CopySymbolicLinksAsLinks)
        {
            badges.Add("copy links");
        }

        if (o.FatTimestampTolerance)
        {
            badges.Add("FAT 2s");
        }

        if (o.ExcludeHiddenSystem)
        {
            badges.Add("no hidden/system");
        }

        if (o.PurgeExtraDestFiles)
        {
            badges.Add("purge extra dest");
        }

        if (!o.IncludeSourceFolderName)
        {
            badges.Add("Contents only");
        }

        return badges;
    }
}
