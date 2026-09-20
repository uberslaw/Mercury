namespace Mercury;

public enum CopyStageKind
{
    PreparingDestination,
    EnumeratingSource,
    CheckingDestinationSpace,
    Transferring,
    Unpacking,
    Pushing,
    Verifying,
    Rundown
}

public readonly record struct CopyStage(CopyStageKind Kind, string Label);

public static class CopyPipeline
{
    public static IReadOnlyList<CopyStage> For(Job job, bool hasJournalFiles, bool pack)
    {
        var catcher = job.Catcher is not null;
        var stages = new List<CopyStage>
        {
            new(CopyStageKind.PreparingDestination, catcher ? "Preparing Catcher send" : "Preparing destination")
        };

        if (!hasJournalFiles)
        {
            stages.Add(new(CopyStageKind.EnumeratingSource, "Enumerating source"));
            if (!catcher)
            {
                stages.Add(new(CopyStageKind.CheckingDestinationSpace, "Checking destination space"));
            }
        }
        else if (job.ScanSourceOnResume)
        {
            stages.Add(new(CopyStageKind.EnumeratingSource, "Checking source for changes"));
        }

        if (!job.Options.DryRun)
        {
            if (catcher)
            {
                if (pack)
                {
                    stages.Add(new(CopyStageKind.Transferring, "Packing"));
                }

                stages.Add(new(CopyStageKind.Pushing, "Sending over HTTPS"));
            }
            else
            {
                stages.Add(new(CopyStageKind.Transferring, pack ? "Packing" : "Copying"));
                if (pack)
                {
                    stages.Add(new(CopyStageKind.Unpacking, "Unpacking"));
                }
            }

            stages.Add(new(CopyStageKind.Verifying, "Verifying"));
        }

        stages.Add(new(CopyStageKind.Rundown, "Writing rundown"));
        return stages;
    }

    public static int IndexOf(IReadOnlyList<CopyStage> stages, CopyStageKind kind)
    {
        for (var i = 0; i < stages.Count; i++)
        {
            if (stages[i].Kind == kind)
            {
                return i + 1;
            }
        }

        return 0;
    }

    public static string LabelOf(IReadOnlyList<CopyStage> stages, CopyStageKind kind)
    {
        foreach (var stage in stages)
        {
            if (stage.Kind == kind)
            {
                return stage.Label;
            }
        }

        return kind.ToString();
    }

    public static string Format(int index, int count, string name) =>
        $"Stage: {index} of {count} — {name}";

    public static string ElapsedLine(TimeSpan overall, TimeSpan stage) =>
        $"Elapsed  {ByteFormatter.Duration(overall)}    this stage  {ByteFormatter.Duration(stage)}";
}
