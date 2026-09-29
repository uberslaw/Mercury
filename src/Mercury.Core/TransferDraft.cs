namespace Mercury;

/// <summary>
/// When the Transfer tab draft should be reset to a fresh not-started job.
/// A failed Start or Add leaves the draft so the user can fix it.
/// </summary>
public enum TransferDraftClear
{
    QueueResume,
    StartSucceeded,
    AddSucceeded,
    StartFailed,
    AddFailed
}

/// <summary>
/// Editable Transfer-tab draft (source, destination, job options).
/// Cleared after queue Resume, a successful Transfer Start, and a successful Transfer Add to queue.
/// The queue row and the journal are left alone.
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

    public static bool ShouldClear(TransferDraftClear when) => when is
        TransferDraftClear.QueueResume
        or TransferDraftClear.StartSucceeded
        or TransferDraftClear.AddSucceeded;

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
