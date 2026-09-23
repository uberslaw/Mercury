namespace Mercury;

public sealed class HistoryItem
{
    public HistoryItem(TransferHistoryEntry entry, bool logExists = false)
    {
        Entry = entry;
        Job = entry.ToJob();
        Rundown = TransferRundown.From(Job);
        var started = entry.StartedUtc is { } s
            ? TransferRundown.FormatLocal(s)
            : "—";
        Title = $"{started}  ·  {Job.Name}";
        Route = $"{entry.SourcePath}  →  {entry.DestinationPath}";
        StatusLabel = entry.Status switch
        {
            JobStatus.Completed => "Completed",
            JobStatus.Incomplete => "Incomplete",
            JobStatus.Cancelled => "Stopped",
            JobStatus.Failed => "Failed",
            _ => entry.Status.ToString()
        };

        var elapsed = Rundown.IsVisible ? Rundown.ElapsedText : "—";
        var files = $"{entry.DestFiles}/{entry.SourceFiles} files";
        var bytes = ByteFormatter.ToString(entry.BytesCopied);
        var speed = ByteFormatter.Speed(entry.AverageBytesPerSecond);
        var match = Rundown.MatchText;
        Summary = string.Join(" · ", new[]
        {
            StatusLabel,
            elapsed,
            files,
            bytes,
            speed,
            match
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var mentionsLog = entry.ResultMessage is not null
            && entry.ResultMessage.Contains("log", StringComparison.OrdinalIgnoreCase);
        CanOpenLog = logExists || mentionsLog;
    }

    public TransferHistoryEntry Entry { get; }
    public Job Job { get; }
    public TransferRundown Rundown { get; }
    public bool HasRundown => Rundown.IsVisible;
    public string Title { get; }
    public string Route { get; }
    public string StatusLabel { get; }
    public string Summary { get; }
    public string RundownLine => Rundown.OneLine;
    public bool CanOpenLog { get; }
}
