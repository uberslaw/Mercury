namespace Mercury;

public enum CompareStageKind
{
    CountSource,
    CountDestination,
    Diff,
    HashSource,
    HashDestination
}

public readonly record struct CompareStage(CompareStageKind Kind, string Label);

public sealed class CompareStageProgress
{
    public CompareStageKind Kind { get; init; }
    public int Index { get; init; }
    public int Count { get; init; }
    public string Name { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
    public TimeSpan OverallElapsed { get; init; }
    public TimeSpan? Remaining { get; init; }
    public double? Percent { get; init; }

    public string Line => Count <= 0 ? Name : ComparePipeline.Format(Index, Count, Name);
}

/// <summary>Real compare pipeline: count each tree, in-memory diff, then optional hashing.</summary>
public static class ComparePipeline
{
    public static IReadOnlyList<CompareStage> For(DirectoryCompareOptions options, bool inventoryComplete = false)
    {
        options ??= DirectoryCompareOptions.Default;
        var hashDest = options.Hash && options.Advanced;
        var hashSource = options.HashSource || hashDest;
        var stages = new List<CompareStage>(5);
        if (!inventoryComplete)
        {
            stages.Add(new(CompareStageKind.CountSource, "Count source"));
            stages.Add(new(CompareStageKind.CountDestination, "Count destination"));
        }

        stages.Add(new(CompareStageKind.Diff, "Diff"));
        if (hashSource)
        {
            stages.Add(new(CompareStageKind.HashSource, "Hash source"));
        }

        if (hashDest)
        {
            stages.Add(new(CompareStageKind.HashDestination, "Hash destination"));
        }

        return stages;
    }

    public static string Format(int index, int count, string name) =>
        $"Stage: {index} of {count} — {name}";

    public static string ClockLine(TimeSpan overall, TimeSpan stage, TimeSpan? remaining) =>
        $"Elapsed  {ByteFormatter.Duration(overall)}    this stage  {ByteFormatter.Duration(stage)}    remaining  {ByteFormatter.Eta(remaining)}";

    public static string OperationLabel(CompareStageKind? kind) => kind switch
    {
        CompareStageKind.CountSource or CompareStageKind.CountDestination => "counting",
        CompareStageKind.Diff => "diff",
        CompareStageKind.HashSource or CompareStageKind.HashDestination => "hashing",
        _ => "Compare"
    };
}
