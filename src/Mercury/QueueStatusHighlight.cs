namespace Mercury;

public enum QueueStatusTone
{
    Queued,
    Transfer,
    Verify,
    Paused,
    Complete,
    Error
}

/// <summary>
/// Maps queue tile status text to a glanceable chip colour.
/// Light fills + dark text; keys live in <see cref="ThemeService.Slots"/>.
/// </summary>
public static class QueueStatusHighlight
{
    public const string TransferBrushKey = "QueueStatusTransferBrush";
    public const string VerifyBrushKey = "QueueStatusVerifyBrush";
    public const string CompleteBrushKey = "QueueStatusCompleteBrush";
    public const string ErrorBrushKey = "QueueStatusErrorBrush";
    public const string PausedBrushKey = "QueueStatusPausedBrush";
    public const string QueuedBrushKey = "QueueStatusQueuedBrush";

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
            _ => QueueStatusTone.Queued
        };
    }

    public static string BrushKey(QueueStatusTone tone) => tone switch
    {
        QueueStatusTone.Transfer => TransferBrushKey,
        QueueStatusTone.Verify => VerifyBrushKey,
        QueueStatusTone.Complete => CompleteBrushKey,
        QueueStatusTone.Error => ErrorBrushKey,
        QueueStatusTone.Paused => PausedBrushKey,
        _ => QueuedBrushKey
    };
}
