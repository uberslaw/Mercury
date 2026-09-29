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
            var missing = Assert.Single(result.Differences, d => d.Kind == CompareDiffKind.OnlyLeftFile);
            Assert.True(missing.IsMissingOnDest);
            Assert.False(missing.IsDestOnly);
            Assert.Equal(CompareListTone.MissingOnDest, missing.ListTone);
            Assert.Equal("CompareMissingOnDestBrush", missing.ListForegroundKey);
            Assert.Contains("source only", missing.KindLabel, StringComparison.Ordinal);
            Assert.DoesNotContain("left", missing.KindLabel, StringComparison.OrdinalIgnoreCase);
            var destOnly = Assert.Single(result.Differences, d => d.Kind == CompareDiffKind.OnlyRightFolder);
            Assert.True(destOnly.IsDestOnly);
            Assert.False(destOnly.IsMissingOnDest);
            Assert.Equal(CompareListTone.DestOnly, destOnly.ListTone);
            Assert.Equal("CompareDestOnlyBrush", destOnly.ListForegroundKey);
            Assert.Contains("dest only", destOnly.KindLabel, StringComparison.Ordinal);
            Assert.DoesNotContain("right", destOnly.KindLabel, StringComparison.OrdinalIgnoreCase);
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
    public void ComparePace_EstimatesRemainingBytes()
    {
        Assert.Null(ComparePace.Estimate(0, 1000, 0));
        Assert.Equal(TimeSpan.FromSeconds(2), ComparePace.Estimate(0, 1000, 500));
        Assert.Equal(TimeSpan.Zero, ComparePace.Estimate(1000, 1000, 500));
        Assert.Equal("c.zip", ComparePace.FileLabel(@"pack\c.zip"));
        Assert.Equal("pack", ComparePace.FolderLabel(@"pack\c.zip"));
        Assert.Equal(".", ComparePace.FolderLabel("root.zip"));
    }

    [Fact]
    public void HashCompare_ReportsFolderAndOverallBytes()
    {
        var (left, right) = Trees();
        try
        {
            Directory.CreateDirectory(Path.Combine(left, "pack"));
            Directory.CreateDirectory(Path.Combine(right, "pack"));
            File.WriteAllText(Path.Combine(left, "root.zip"), "AAAA");
            File.WriteAllText(Path.Combine(right, "root.zip"), "AAAA");
            File.WriteAllText(Path.Combine(left, "pack", "a.zip"), "BBBB");
            File.WriteAllText(Path.Combine(right, "pack", "a.zip"), "BBBB");
            File.WriteAllText(Path.Combine(left, "pack", "b.zip"), "CCCC");
            File.WriteAllText(Path.Combine(right, "pack", "b.zip"), "DDDD");
            File.WriteAllText(Path.Combine(left, "skip.bin"), "short");
            File.WriteAllText(Path.Combine(right, "skip.bin"), "different-length");

            var pulses = new List<DirectoryCompareProgress>();
            var result = DirectoryComparer.Compare(
                left,
                right,
                new DirectoryCompareOptions { Advanced = true, Hash = true },
                progress: pulses.Add);
            Assert.True(result.Completed);
            Assert.Equal(1, result.HashMismatches);
            Assert.Equal(1, result.SizeMismatches);
            var hashing = pulses.Where(p => p.Pace is { BytesTotal: > 0 }).ToList();
            Assert.NotEmpty(hashing);
            Assert.All(hashing, p => Assert.Equal(24, p.Pace!.BytesTotal));
            Assert.Contains(hashing, p => p.Pace!.CurrentFolder == "pack" && p.Pace.FolderBytesTotal == 16);
            Assert.Equal(24, hashing[^1].Pace!.BytesDone);
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
            Assert.Contains("Source:", txt, StringComparison.Ordinal);
            Assert.Contains("Destination:", txt, StringComparison.Ordinal);
            Assert.Contains("=== Destination-only folders", txt, StringComparison.Ordinal);
            Assert.Contains("=== Source-only files", txt, StringComparison.Ordinal);
            Assert.DoesNotContain("Left:", txt, StringComparison.Ordinal);
            Assert.DoesNotContain("Right:", txt, StringComparison.Ordinal);
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
        Assert.Contains("current folder", section.Body, StringComparison.Ordinal);
        Assert.Contains("each source hash", section.Body, StringComparison.Ordinal);
        Assert.Contains("starts the count over", section.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("does not keep a place to resume", section.Body, StringComparison.Ordinal);
        Assert.Contains("Source / Destination", section.Body, StringComparison.Ordinal);
        Assert.Contains("Source→Dest", section.Body, StringComparison.Ordinal);
        Assert.Contains("Show destination-only", section.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Left / Right", section.Body, StringComparison.Ordinal);
        Assert.Contains(HelpDocument.Search("Compare"), s => s.Id == "compare");
        Assert.Contains("Compare", HelpDocument.Sections.Single(s => s.Id == "start").Body, StringComparison.Ordinal);
    }

    [Fact]
    public void DestOnlyToggleHidesFromListAndCards_SummaryAndReportStillInclude()
    {
        var (left, right) = Trees();
        try
        {
            File.WriteAllText(Path.Combine(left, "only-source.txt"), "solo");
            Directory.CreateDirectory(Path.Combine(right, "only-dest-dir"));
            File.WriteAllText(Path.Combine(right, "only-dest.txt"), "extra");

            var result = DirectoryComparer.Compare(left, right);
            var filter = CompareFilter.FolderCounts | CompareFilter.FileCounts;
            Assert.Contains(result.Listed(filter, 2_000, includeDestOnly: true), d => d.IsDestOnly);
            Assert.DoesNotContain(result.Listed(filter, 2_000, includeDestOnly: false), d => d.IsDestOnly);
            Assert.Contains(result.Listed(filter, 2_000, includeDestOnly: false), d => d.IsMissingOnDest);
            Assert.Contains(result.Highlights(filter, 5, includeDestOnly: true), h => h.IsDestOnly);
            Assert.DoesNotContain(result.Highlights(filter, 5, includeDestOnly: false), h => h.IsDestOnly);
            Assert.Contains(result.Highlights(filter, 5, includeDestOnly: false), h => h.IsMissingOnDest);
            Assert.Equal(1, result.FilesOnlyRight);
            Assert.Equal(1, result.FoldersOnlyRight);
            var txt = DirectoryCompareReport.Build(result, filter);
            Assert.Contains("only-dest.txt", txt, StringComparison.Ordinal);
            Assert.Contains("only-dest-dir", txt, StringComparison.Ordinal);
            Assert.Contains("=== Destination-only files", txt, StringComparison.Ordinal);
            Assert.Contains("=== Destination-only folders", txt, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(left, right);
        }
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

    [Fact]
    public void Manifest_RoundTrip_LastHashWins_AndSameJob()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mercury-manifest-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(dir, "L");
        var right = Path.Combine(dir, "R");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        try
        {
            var path = Path.Combine(dir, "compare-manifest.jsonl");
            using (var writer = CompareManifestWriter.Open(path, left, right, advanced: true, hash: true, fat: false))
            {
                writer.WriteInventory(new CompareManifest
                {
                    LeftRootFiles = 1,
                    RightRootFiles = 1,
                    Files =
                    {
                        new CompareManifestEntry
                        {
                            Left = true,
                            Relative = "a.txt",
                            Name = "a.txt",
                            FullPath = Path.Combine(left, "a.txt"),
                            Size = 4,
                            WriteTicks = 10
                        },
                        new CompareManifestEntry
                        {
                            Left = false,
                            Relative = "a.txt",
                            Name = "a.txt",
                            FullPath = Path.Combine(right, "a.txt"),
                            Size = 4,
                            WriteTicks = 10
                        }
                    }
                });
                writer.WriteHash(left: true, "a.txt", "AAAA", 4, 10);
                writer.WriteHash(left: true, "a.txt", "BBBB", 4, 10);
            }

            var loaded = CompareManifestStore.Load(path);
            Assert.NotNull(loaded);
            Assert.True(loaded!.InventoryComplete);
            Assert.Equal(1, loaded.LeftRootFiles);
            Assert.True(CompareManifestStore.SameJob(loaded, left, right, true, true, false));
            Assert.False(CompareManifestStore.SameJob(loaded, left, right, true, false, false));
            Assert.False(CompareManifestStore.SameJob(loaded, left, right, true, true, true));
            Assert.False(CompareManifestStore.SameJob(loaded, left, Path.Combine(dir, "other"), true, true, false));
            var entry = Assert.Single(loaded.Files, file => file.Left);
            Assert.Equal("BBBB", entry.ContentHash);
            Assert.Equal(1, loaded.SourceHashCount);
            Assert.Equal(0, loaded.DestHashCount);
            Assert.Equal(0, loaded.ComparedFileCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void HashResume_SkipsFinishedSourceHash()
    {
        var (left, right) = Trees();
        try
        {
            File.WriteAllText(Path.Combine(left, "a.txt"), "AAAA");
            File.WriteAllText(Path.Combine(right, "a.txt"), "AAAA");
            File.WriteAllText(Path.Combine(left, "b.txt"), "BBBB");
            File.WriteAllText(Path.Combine(right, "b.txt"), "BBBB");
            var manifest = Path.Combine(Path.GetDirectoryName(left)!, "compare-manifest.jsonl");
            using var cts = new CancellationTokenSource();
            var first = DirectoryComparer.Compare(
                left,
                right,
                new DirectoryCompareOptions { Advanced = true, Hash = true },
                cts.Token,
                progress: progress =>
                {
                    if (progress.CurrentRelative == "a.txt" &&
                        progress.Pace is { FileBytesDone: > 0, FileBytesTotal: > 0 } &&
                        progress.Pace.FileBytesDone == progress.Pace.FileBytesTotal)
                    {
                        cts.Cancel();
                    }
                },
                manifestPath: manifest);
            Assert.True(first.Canceled);
            var saved = CompareManifestStore.Load(manifest);
            Assert.NotNull(saved);
            Assert.True(saved!.InventoryComplete);
            Assert.Contains(saved.Files, file => file.Left && file.Relative == "a.txt" && !string.IsNullOrEmpty(file.ContentHash));
            Assert.DoesNotContain(saved.Files, file => !file.Left && file.Relative == "a.txt" && !string.IsNullOrEmpty(file.ContentHash));
            Assert.DoesNotContain(saved.Files, file => file.Relative == "b.txt" && !string.IsNullOrEmpty(file.ContentHash));

            var starts = new List<string>();
            var second = DirectoryComparer.Compare(
                left,
                right,
                new DirectoryCompareOptions { Advanced = true, Hash = true },
                progress: progress =>
                {
                    if (progress.Pace is { FileBytesDone: 0, FileBytesTotal: > 0 })
                    {
                        starts.Add(progress.CurrentRelative ?? "");
                    }
                },
                manifestPath: manifest);
            Assert.True(second.Completed);
            Assert.Equal(0, second.HashMismatches);
            Assert.Equal(["b.txt", "a.txt", "b.txt"], starts);
            var finished = CompareManifestStore.Load(manifest);
            Assert.Equal(2, finished!.ComparedFileCount);
        }
        finally
        {
            Cleanup(left, right);
        }
    }

    [Fact]
    public void Manifest_RehashesDestinationWhenFileChanges()
    {
        var (left, right) = Trees();
        try
        {
            var leftFile = Path.Combine(left, "a.txt");
            var rightFile = Path.Combine(right, "a.txt");
            File.WriteAllText(leftFile, "AAAA");
            File.WriteAllText(rightFile, "AAAA");
            var manifest = Path.Combine(Path.GetDirectoryName(left)!, "compare-manifest.jsonl");
            var first = DirectoryComparer.Compare(
                left,
                right,
                new DirectoryCompareOptions { Advanced = true, Hash = true },
                manifestPath: manifest);
            Assert.True(first.Completed);
            Assert.Equal(0, first.HashMismatches);

            File.WriteAllText(rightFile, "BBBB");
            File.SetLastWriteTimeUtc(rightFile, DateTime.UtcNow.AddMinutes(5));
            var starts = new List<string>();
            var second = DirectoryComparer.Compare(
                left,
                right,
                new DirectoryCompareOptions { Advanced = true, Hash = true },
                progress: progress =>
                {
                    if (progress.Pace is { FileBytesDone: 0, FileBytesTotal: > 0 })
                    {
                        starts.Add(progress.CurrentRelative ?? "");
                    }
                },
                manifestPath: manifest);
            Assert.True(second.Completed);
            Assert.Equal(1, second.HashMismatches);
            Assert.Equal(["a.txt"], starts);
        }
        finally
        {
            Cleanup(left, right);
        }
    }

    [Fact]
    public void CancelDuringCount_DoesNotKeepFileList()
    {
        var (left, right) = Trees();
        try
        {
            for (var i = 0; i < 50; i++)
            {
                var name = "f" + i.ToString("00") + ".txt";
                File.WriteAllText(Path.Combine(left, name), "x");
                File.WriteAllText(Path.Combine(right, name), "x");
            }

            var manifest = Path.Combine(Path.GetDirectoryName(left)!, "compare-manifest.jsonl");
            using var cts = new CancellationTokenSource();
            var canceled = DirectoryComparer.Compare(
                left,
                right,
                new DirectoryCompareOptions { Advanced = true, Hash = true },
                cts.Token,
                progress: progress =>
                {
                    if (progress.FilesVisited >= 50 && progress.Pace is null)
                    {
                        cts.Cancel();
                    }
                },
                manifestPath: manifest);
            Assert.True(canceled.Canceled);
            var partial = CompareManifestStore.Load(manifest);
            Assert.True(partial is null || !partial.InventoryComplete);

            var resumed = DirectoryComparer.Compare(
                left,
                right,
                new DirectoryCompareOptions { Advanced = true, Hash = true },
                manifestPath: manifest);
            var fresh = DirectoryComparer.Compare(
                left,
                right,
                new DirectoryCompareOptions { Advanced = true, Hash = true });
            Assert.True(resumed.Completed);
            Assert.Equal(fresh.LeftFiles, resumed.LeftFiles);
            Assert.Equal(fresh.RightFiles, resumed.RightFiles);
            Assert.Equal(fresh.HashMismatches, resumed.HashMismatches);
            Assert.Equal(fresh.FilesSameRelativePath, resumed.FilesSameRelativePath);
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
