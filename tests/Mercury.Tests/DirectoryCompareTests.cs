namespace Mercury.Tests;

public class DirectoryCompareTests
{
    [Fact]
    public void DefaultCounts_OnlyLeftFile_OnlyRightFolder_SameFile()
    {
        var (left, right) = Trees();
        try
        {
            File.WriteAllText(Path.Combine(left, "shared.txt"), "same");
            File.WriteAllText(Path.Combine(right, "shared.txt"), "same");
            File.WriteAllText(Path.Combine(left, "only-left.txt"), "solo");
            Directory.CreateDirectory(Path.Combine(right, "only-right-dir"));

            var result = DirectoryComparer.Compare(left, right);
            Assert.True(result.Completed);
            Assert.False(result.Advanced);
            Assert.False(result.Hashed);
            Assert.Equal(0, result.LeftFolders);
            Assert.Equal(1, result.RightFolders);
            Assert.Equal(0, result.FoldersOnlyLeft);
            Assert.Equal(1, result.FoldersOnlyRight);
            Assert.Equal(0, result.FoldersMatchingName);
            Assert.Equal(2, result.LeftFiles);
            Assert.Equal(1, result.RightFiles);
            Assert.Equal(1, result.FilesOnlyLeft);
            Assert.Equal(0, result.FilesOnlyRight);
            Assert.Equal(1, result.FilesSameRelativePath);
            Assert.Equal(0, result.SizeMismatches);
            Assert.Contains(result.Differences, d => d.Kind == CompareDiffKind.OnlyLeftFile && d.RelativePath == "only-left.txt");
            Assert.Contains(result.Differences, d => d.Kind == CompareDiffKind.OnlyRightFolder && d.RelativePath == "only-right-dir");
            Assert.DoesNotContain(result.Differences, d => d.Kind == CompareDiffKind.SizeMismatch);
        }
        finally
        {
            Cleanup(left, right);
        }
    }

    [Fact]
    public void Advanced_SizeMismatch()
    {
        var (left, right) = Trees();
        try
        {
            File.WriteAllText(Path.Combine(left, "shared.bin"), new string('a', 10));
            File.WriteAllText(Path.Combine(right, "shared.bin"), new string('b', 40));

            var result = DirectoryComparer.Compare(left, right, new DirectoryCompareOptions { Advanced = true });
            Assert.True(result.Completed);
            Assert.Equal(1, result.FilesSameRelativePath);
            Assert.Equal(1, result.SizeMismatches);
            Assert.Contains(result.Differences, d => d.Kind == CompareDiffKind.SizeMismatch && d.RelativePath == "shared.bin");
        }
        finally
        {
            Cleanup(left, right);
        }
    }

    [Fact]
    public void Advanced_HashMismatch_SameSize()
    {
        var (left, right) = Trees();
        try
        {
            File.WriteAllText(Path.Combine(left, "payload.txt"), "AAAA");
            File.WriteAllText(Path.Combine(right, "payload.txt"), "BBBB");

            var result = DirectoryComparer.Compare(
                left,
                right,
                new DirectoryCompareOptions { Advanced = true, Hash = true });
            Assert.True(result.Completed);
            Assert.True(result.Hashed);
            Assert.Equal(1, result.FilesSameRelativePath);
            Assert.Equal(0, result.SizeMismatches);
            Assert.Equal(1, result.HashMismatches);
            Assert.Contains(result.Differences, d => d.Kind == CompareDiffKind.HashMismatch && d.RelativePath == "payload.txt");
        }
        finally
        {
            Cleanup(left, right);
        }
    }

    [Fact]
    public void FilterHidesSizeDiffsWhenSizeFilterOff()
    {
        var (left, right) = Trees();
        try
        {
            File.WriteAllText(Path.Combine(left, "only-left.txt"), "x");
            File.WriteAllText(Path.Combine(left, "shared.bin"), "aa");
            File.WriteAllText(Path.Combine(right, "shared.bin"), "bbbb");

            var result = DirectoryComparer.Compare(left, right, new DirectoryCompareOptions { Advanced = true });
            Assert.True(result.SizeMismatches > 0);
            var withoutSize = CompareFilter.FolderCounts | CompareFilter.FileCounts;
            Assert.DoesNotContain(result.Filtered(withoutSize), d => d.Kind == CompareDiffKind.SizeMismatch);
            Assert.Contains(result.Filtered(withoutSize), d => d.Kind == CompareDiffKind.OnlyLeftFile);
            Assert.Contains(result.Filtered(withoutSize | CompareFilter.Size), d => d.Kind == CompareDiffKind.SizeMismatch);
            var txt = DirectoryCompareReport.Build(result, withoutSize);
            Assert.DoesNotContain("=== Size mismatches", txt, StringComparison.Ordinal);
            Assert.Contains("only-left.txt", txt, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(left, right);
        }
    }

    [Fact]
    public async Task CancelDuringPause()
    {
        var (left, right) = Trees();
        try
        {
            File.WriteAllText(Path.Combine(left, "a.txt"), "a");
            File.WriteAllText(Path.Combine(right, "a.txt"), "a");
            var pause = new PauseGate();
            pause.Pause();
            using var cts = new CancellationTokenSource();
            var task = Task.Run(() => DirectoryComparer.Compare(left, right, DirectoryCompareOptions.Default, cts.Token, pause));
            await Task.Delay(80);
            cts.Cancel();
            var result = await task;
            Assert.True(result.Canceled);
            Assert.False(result.Completed);
        }
        finally
        {
            Cleanup(left, right);
        }
    }

    [Fact]
    public void ReportContainsOnlyLeftPathAndDefaultFileName()
    {
        var (left, right) = Trees();
        try
        {
            File.WriteAllText(Path.Combine(left, "only-left.txt"), "solo");
            Directory.CreateDirectory(Path.Combine(right, "only-right-dir"));
            var result = DirectoryComparer.Compare(left, right);
            var txt = DirectoryCompareReport.Build(result, CompareFilter.FolderCounts | CompareFilter.FileCounts);
            Assert.Contains("only-left.txt", txt, StringComparison.Ordinal);
            Assert.Contains("only-right-dir", txt, StringComparison.Ordinal);
            Assert.Contains(left, txt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(right, txt, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Default counts", txt, StringComparison.Ordinal);
            var name = DirectoryCompareReport.DefaultFileName(new DateTime(2026, 9, 24, 6, 5, 0));
            Assert.Equal("compare-20260924-0605.txt", name);
            var path = Path.Combine(Path.GetTempPath(), name);
            DirectoryCompareReport.Write(path, result, CompareFilter.FolderCounts | CompareFilter.FileCounts);
            Assert.Contains("only-left.txt", File.ReadAllText(path), StringComparison.Ordinal);
            File.Delete(path);
        }
        finally
        {
            Cleanup(left, right);
        }
    }

    [Fact]
    public void HelpMentionsCompareExportAndCreateJob()
    {
        var section = HelpDocument.Sections.Single(s => s.Id == "compare");
        Assert.Contains("Export TXT", section.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compare-YYYYMMDD-HHMM.txt", section.Body, StringComparison.Ordinal);
        Assert.Contains("Add missing to queue", section.Body, StringComparison.Ordinal);
        Assert.Contains("does not start", section.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Skip if dest newer or equal", section.Body, StringComparison.Ordinal);
        Assert.Contains("xxHash64", section.Body, StringComparison.Ordinal);
        Assert.Contains(HelpDocument.Search("Compare"), s => s.Id == "compare");
        Assert.Contains("Compare", HelpDocument.Sections.Single(s => s.Id == "start").Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Advanced_PerFolderFileCountMismatch()
    {
        var (left, right) = Trees();
        try
        {
            var leftSub = Directory.CreateDirectory(Path.Combine(left, "album")).FullName;
            var rightSub = Directory.CreateDirectory(Path.Combine(right, "album")).FullName;
            File.WriteAllText(Path.Combine(leftSub, "a.jpg"), "a");
            File.WriteAllText(Path.Combine(leftSub, "b.jpg"), "b");
            File.WriteAllText(Path.Combine(rightSub, "a.jpg"), "a");

            var result = DirectoryComparer.Compare(left, right, new DirectoryCompareOptions { Advanced = true });
            Assert.Equal(1, result.FolderFileCountMismatches);
            Assert.Contains(result.Differences, d =>
                d.Kind == CompareDiffKind.FolderFileCountMismatch &&
                d.RelativePath == "album" &&
                d.LeftFileCount == 2 &&
                d.RightFileCount == 1);
        }
        finally
        {
            Cleanup(left, right);
        }
    }

    private static (string Left, string Right) Trees()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-compare-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        return (left, right);
    }

    private static void Cleanup(string left, string right)
    {
        try
        {
            var root = Path.GetDirectoryName(left);
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
        catch
        {
            try
            {
                Directory.Delete(left, true);
                Directory.Delete(right, true);
            }
            catch
            {
                // temp leftover is OK
            }
        }
    }
}
