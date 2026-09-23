namespace Mercury;

public enum QueueStatusTone
{
    Queued,
    Transfer,
    Verify,
    Paused,
    PauseAfter,
    Complete,
    Error
}

public sealed class StatusChipView
{
    public StatusChipView(string tileStatus)
    {
        TileStatus = tileStatus;
        StatusTone = QueueStatusHighlight.ForLabel(tileStatus);
    }

    public string TileStatus { get; }

    public QueueStatusTone StatusTone { get; }
}

/// <summary>
/// Maps queue/header status text to a glanceable chip colour.
/// Light fills + dark text; keys live in <see cref="ThemeService.Slots"/>.
/// </summary>
public static class QueueStatusHighlight
{
    public const string TransferBrushKey = "QueueStatusTransferBrush";
    public const string VerifyBrushKey = "QueueStatusVerifyBrush";
    public const string CompleteBrushKey = "QueueStatusCompleteBrush";
    public const string ErrorBrushKey = "QueueStatusErrorBrush";
    public const string PausedBrushKey = "QueueStatusPausedBrush";
    public const string PauseAfterBrushKey = "QueueStatusPauseAfterBrush";
    public const string QueuedBrushKey = "QueueStatusQueuedBrush";

    public const string IdleLabel = "Not started";
    public const string PauseAfterLabel = "Pausing after file";

    public static QueueStatusTone For(QueueJobItem item) => ForLabel(item.TileStatus);

    public static QueueStatusTone ForLabel(string? tileStatus)
    {
        if (string.IsNullOrWhiteSpace(tileStatus))
        {
            return QueueStatusTone.Queued;
        }

        return tileStatus.Trim() switch
        {
            "Transferring" or "Copying" or "Preparing" or "Enumerating" or "Running" => QueueStatusTone.Transfer,
            "Verifying" or "Rundown" => QueueStatusTone.Verify,
            "Done" or "Complete" or "Completed" or "Finished" => QueueStatusTone.Complete,
            "Incomplete" or "Failed" or "Error" => QueueStatusTone.Error,
            "Paused" or "Waiting for hours" => QueueStatusTone.Paused,
            PauseAfterLabel or "Pause after this file" or "Pausing after this file" => QueueStatusTone.PauseAfter,
            IdleLabel or "Idle" or "Pending" or "On hold" or "Stopped" or "Queued" => QueueStatusTone.Queued,
            _ => QueueStatusTone.Queued
        };
    }

    public static string ForRun(
        bool isRunning,
        bool isPaused,
        bool pauseAfterArmed,
        bool backgroundRundown,
        bool verifying,
        bool rundown,
        JobStatus? liveStatus)
    {
        if (isPaused)
        {
            return "Paused";
        }

        if (!isRunning)
        {
            if (backgroundRundown)
            {
                return verifying ? "Verifying" : "Rundown";
            }

            return IdleLabel;
        }

        if (pauseAfterArmed)
        {
            return PauseAfterLabel;
        }

        if (rundown)
        {
            return "Rundown";
        }

        if (verifying || liveStatus == JobStatus.Verifying)
        {
            return "Verifying";
        }

        return liveStatus switch
        {
            JobStatus.Preparing => "Preparing",
            JobStatus.Enumerating => "Enumerating",
            JobStatus.Copying => "Transferring",
            JobStatus.Paused or JobStatus.PausedOutsideHours => "Paused",
            _ => "Transferring"
        };
    }

    public static string BrushKey(QueueStatusTone tone) => tone switch
    {
        QueueStatusTone.Transfer => TransferBrushKey,
        QueueStatusTone.Verify => VerifyBrushKey,
        QueueStatusTone.Complete => CompleteBrushKey,
        QueueStatusTone.Error => ErrorBrushKey,
        QueueStatusTone.Paused => PausedBrushKey,
        QueueStatusTone.PauseAfter => PauseAfterBrushKey,
        _ => QueuedBrushKey
    };
}
