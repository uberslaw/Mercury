namespace Mercury;

/// <summary>
/// Editable Transfer-tab draft (source, destination, job options). Queue Resume clears this
/// so the tab is a fresh not-started job. The queue row and the journal are left alone.
/// </summary>
public sealed class TransferDraft
{
    public string SourcePath { get; set; } = "";
    public List<string> SourcePaths { get; set; } = [];
    public string DestinationPath { get; set; } = "";
    public int DestinationKind { get; set; }
    public JobOptions Options { get; set; } = new();
    public bool ScheduleEnabled { get; set; }
    public DateTime? ScheduledDate { get; set; } = DateTime.Today;
    public string ScheduledTime { get; set; } = "09:00";

    public void ClearForQueueResume()
    {
        SourcePath = "";
        SourcePaths = [];
        DestinationPath = "";
        DestinationKind = 0;
        Options = new JobOptions();
        ScheduleEnabled = false;
        ScheduledDate = DateTime.Today;
        ScheduledTime = "09:00";
    }

    public bool IsCleared =>
        string.IsNullOrEmpty(SourcePath)
        && SourcePaths.Count == 0
        && string.IsNullOrEmpty(DestinationPath)
        && DestinationKind == 0
        && !ScheduleEnabled
        && Options.FixLongOrDuplicateNames
        && !Options.DryRun
        && !Options.HoursEnabled
        && Options.ScheduleDays is null;
}
