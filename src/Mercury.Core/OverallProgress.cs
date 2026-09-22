namespace Mercury;

/// <summary>
/// Queue-wide byte totals. A finished job with no live snapshot still counts its
/// copied size as both done and total so Overall cannot read 100% from job 1's
/// payload over job 2's total alone.
/// </summary>
internal static class OverallProgress
{
    public static void AddBytes(Job job, JobProgress? progress, ref long copied, ref long total)
    {
        if (progress is not null)
        {
            copied += progress.BytesCopied;
            total += ByteTotal(job, progress);
            return;
        }

        copied += job.BytesCopied;
        if (CountsAsFinished(job.Status))
        {
            total += job.BytesCopied;
        }
    }

    public static double Percent(long copied, long total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return Math.Clamp(100.0 * copied / total, 0, 100);
    }

    private static long ByteTotal(Job job, JobProgress progress)
    {
        if (progress.BytesTotal > 0)
        {
            return progress.BytesTotal;
        }

        return CountsAsFinished(job.Status) ? progress.BytesCopied : 0;
    }

    private static bool CountsAsFinished(JobStatus status) =>
        status is JobStatus.Completed or JobStatus.Incomplete or JobStatus.Cancelled or JobStatus.Failed;
}
