namespace Mercury.Tests;

public class CompareProgressTests
{
    [Fact]
    public void ConsoleFollow_StaysOnWhenContentGrows_TurnsOffWhenUserScrollsAway()
    {
        Assert.True(ConsoleFollow.AfterScroll(following: true, programmatic: false, extentHeightChange: 40, verticalChange: 0, atBottom: false));
        Assert.True(ConsoleFollow.AfterScroll(following: true, programmatic: true, extentHeightChange: 0, verticalChange: -12, atBottom: false));
        Assert.True(ConsoleFollow.AfterScroll(following: true, programmatic: false, extentHeightChange: 0, verticalChange: 0, atBottom: false));
        Assert.False(ConsoleFollow.AfterScroll(following: true, programmatic: false, extentHeightChange: 0, verticalChange: -20, atBottom: false));
        Assert.True(ConsoleFollow.AfterScroll(following: false, programmatic: false, extentHeightChange: 0, verticalChange: 20, atBottom: true));
        Assert.True(ConsoleFollow.IsAtBottom(100, 100));
        Assert.True(ConsoleFollow.IsAtBottom(80, 100, slack: 32));
        Assert.False(ConsoleFollow.IsAtBottom(10, 100, slack: 32));
        Assert.True(ConsoleFollow.IsAtBottom(0, 0));
    }

    [Fact]
    public void ComparePipeline_MatchesOptionsAndSavedInventory()
    {
        var basic = ComparePipeline.For(DirectoryCompareOptions.Default);
        Assert.Equal(
            [CompareStageKind.CountSource, CompareStageKind.CountDestination, CompareStageKind.Diff],
            basic.Select(s => s.Kind));

        var hashed = ComparePipeline.For(new DirectoryCompareOptions { Advanced = true, Hash = true });
        Assert.Equal(
            [
                CompareStageKind.CountSource,
                CompareStageKind.CountDestination,
                CompareStageKind.Diff,
                CompareStageKind.HashSource,
                CompareStageKind.HashDestination
            ],
            hashed.Select(s => s.Kind));

        var sourceOnly = ComparePipeline.For(new DirectoryCompareOptions { HashSource = true });
        Assert.Equal(CompareStageKind.HashSource, sourceOnly[^1].Kind);
        Assert.DoesNotContain(sourceOnly, s => s.Kind == CompareStageKind.HashDestination);

        var resume = ComparePipeline.For(new DirectoryCompareOptions { Advanced = true, Hash = true }, inventoryComplete: true);
        Assert.Equal(
            [CompareStageKind.Diff, CompareStageKind.HashSource, CompareStageKind.HashDestination],
            resume.Select(s => s.Kind));
        Assert.Equal("Stage: 2 of 4 — Count destination", ComparePipeline.Format(2, 4, "Count destination"));
        Assert.Contains("remaining", ComparePipeline.ClockLine(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(5), null), StringComparison.Ordinal);
        Assert.Contains("—", ComparePipeline.ClockLine(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(5), null), StringComparison.Ordinal);
    }

    [Fact]
    public void CompareHeaderBars_CountingFileIsInstant_HashingUsesBytesAndClocks()
    {
        var counting = CompareHeaderBars.From(new DirectoryCompareProgress(
            12,
            2,
            @"Warren Truss\compressed\a.zip",
            new ComparePace
            {
                CurrentFolder = @"Warren Truss\compressed",
                FileInstant = true,
                OverallElapsed = TimeSpan.FromSeconds(9)
            },
            new CompareStageProgress
            {
                Kind = CompareStageKind.CountDestination,
                Index = 2,
                Count = 3,
                Name = "Count destination",
                Elapsed = TimeSpan.FromSeconds(4),
                OverallElapsed = TimeSpan.FromSeconds(9)
            }));
        Assert.True(counting.OverallIndeterminate);
        Assert.False(counting.FileIndeterminate);
        Assert.Equal(100, counting.FilePercent);
        Assert.Contains("File", counting.FileText, StringComparison.Ordinal);
        Assert.Contains("a.zip", counting.FileText, StringComparison.Ordinal);
        Assert.Contains("100%", counting.FileText, StringComparison.Ordinal);
        Assert.Contains("Warren Truss\\compressed", counting.FolderText, StringComparison.Ordinal);
        Assert.Equal("Compare  counting", counting.OverallText);
        Assert.Equal("—", counting.OverallRemaining);
        Assert.Equal("9s", counting.OverallElapsed);

        var hashing = CompareHeaderBars.From(new DirectoryCompareProgress(
            12,
            2,
            @"pack\c.zip",
            new ComparePace
            {
                CurrentFolder = "pack",
                FileBytesDone = 50,
                FileBytesTotal = 100,
                FolderBytesDone = 50,
                FolderBytesTotal = 200,
                BytesDone = 100,
                BytesTotal = 400,
                BytesPerSecond = 50,
                FileElapsed = TimeSpan.FromSeconds(2),
                FolderElapsed = TimeSpan.FromSeconds(4),
                OverallElapsed = TimeSpan.FromSeconds(10)
            },
            new CompareStageProgress
            {
                Kind = CompareStageKind.HashSource,
                Index = 4,
                Count = 5,
                Name = "Hash source"
            }));
        Assert.False(hashing.OverallIndeterminate);
        Assert.Equal(50, hashing.FilePercent);
        Assert.Equal(25, hashing.FolderPercent);
        Assert.Equal(25, hashing.OverallPercent);
        Assert.Contains("50%", hashing.FileText, StringComparison.Ordinal);
        Assert.Contains("hashing", hashing.OverallText, StringComparison.Ordinal);
        Assert.Contains("25%", hashing.OverallText, StringComparison.Ordinal);
        Assert.NotEqual("—", hashing.OverallRemaining);
    }

    [Fact]
    public void DefaultCompare_EmitsCountThenDiffStages()
    {
        var (left, right) = Trees();
        try
        {
            Directory.CreateDirectory(Path.Combine(left, "pack"));
            File.WriteAllText(Path.Combine(left, "a.txt"), "aa");
            File.WriteAllText(Path.Combine(left, "pack", "b.txt"), "bb");
            File.WriteAllText(Path.Combine(right, "a.txt"), "aa");

            var pulses = new List<DirectoryCompareProgress>();
            var result = DirectoryComparer.Compare(left, right, progress: pulses.Add);
            Assert.True(result.Completed);
            var kinds = pulses.Where(p => p.Stage is not null).Select(p => p.Stage!.Kind).Distinct().ToList();
            Assert.Equal(
                [CompareStageKind.CountSource, CompareStageKind.CountDestination, CompareStageKind.Diff],
                kinds);
            Assert.Contains(pulses, p => p.Stage is { Index: 1, Count: 3, Name: "Count source" });
            Assert.Contains(pulses, p => p.Stage is { Index: 2, Count: 3 });
            Assert.Contains(pulses, p => p.Stage?.Percent is null && p.Stage?.Kind == CompareStageKind.CountSource);
            Assert.Contains(pulses, p => p.Stage is { Kind: CompareStageKind.Diff, Percent: not null });
        }
        finally
        {
            Cleanup(left, right);
        }
    }

    [Fact]
    public void HashCompare_EmitsHashStagesAfterDiff()
    {
        var (left, right) = Trees();
        try
        {
            File.WriteAllText(Path.Combine(left, "a.txt"), "AAAA");
            File.WriteAllText(Path.Combine(right, "a.txt"), "AAAA");
            var pulses = new List<DirectoryCompareProgress>();
            var result = DirectoryComparer.Compare(
                left,
                right,
                new DirectoryCompareOptions { Advanced = true, Hash = true },
                progress: pulses.Add);
            Assert.True(result.Completed);
            var kinds = pulses.Where(p => p.Stage is not null).Select(p => p.Stage!.Kind).Distinct().ToList();
            Assert.Contains(CompareStageKind.Diff, kinds);
            Assert.Contains(CompareStageKind.HashSource, kinds);
            Assert.Contains(CompareStageKind.HashDestination, kinds);
            Assert.Equal(5, pulses.First(p => p.Stage is not null).Stage!.Count);
            Assert.Contains(pulses, p => p.Pace is { StageBytesTotal: > 0, BytesTotal: > 0 });
        }
        finally
        {
            Cleanup(left, right);
        }
    }

    private static (string Left, string Right) Trees()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-compare-prog-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        return (left, right);
    }

    private static void Cleanup(string left, string right)
    {
        var root = Path.GetDirectoryName(left);
        try
        {
            if (root is not null)
            {
                Directory.Delete(root, true);
            }
        }
        catch
        {
            try { Directory.Delete(left, true); } catch { /* leftover temp */ }
            try { Directory.Delete(right, true); } catch { /* leftover temp */ }
        }
    }
}
