namespace Mercury.Tests;

public class CompareQueueTests
{
    [Fact]
    public void CompareHeaderBarsUseFileFolderAndOverallPace()
    {
        var counting = CompareHeaderBars.From(new DirectoryCompareProgress(3, 1, @"album\a.txt"));
        Assert.True(counting.Indeterminate);
        Assert.Equal("a.txt", counting.FileText);
        Assert.Equal("album", counting.FolderText);
        Assert.Equal("Compare  (counting)", counting.OverallText);

        var pace = new ComparePace
        {
            CurrentFolder = "album",
            FileBytesDone = 25,
            FileBytesTotal = 100,
            FolderBytesDone = 50,
            FolderBytesTotal = 200,
            BytesDone = 100,
            BytesTotal = 400
        };
        var bars = CompareHeaderBars.From(new DirectoryCompareProgress(4, 1, @"album\a.txt", pace));
        Assert.False(bars.Indeterminate);
        Assert.Equal(25, bars.FilePercent);
        Assert.Equal(25, bars.FolderPercent);
        Assert.Equal(25, bars.OverallPercent);
        Assert.Contains("a.txt", bars.FileText, StringComparison.Ordinal);
        Assert.Contains("album", bars.FolderText, StringComparison.Ordinal);
        Assert.StartsWith("Compare", bars.OverallText, StringComparison.Ordinal);
    }

    [Fact]
    public void MainHeaderCompareBarsAreOneWayAndLeaveTransferBars()
    {
        var xaml = File.ReadAllText(FindRepoFile(Path.Combine("src", "Mercury", "MainWindow.xaml")));
        Assert.Contains("x:Name=\"HeaderCompareBars\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"{Binding ShowCompareHeader, Converter={StaticResource BoolToVis}}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HeaderCompareFileBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding CompareFilePercent, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsIndeterminate=\"{Binding CompareFileIndeterminate, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HeaderCompareFolderBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding CompareFolderPercent, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsIndeterminate=\"{Binding CompareFolderIndeterminate, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"HeaderCompareOverallBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding CompareOverallPercent, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsIndeterminate=\"{Binding CompareOverallIndeterminate, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        var transfer = xaml.IndexOf("x:Name=\"HeaderCurrentProgressBar\"", StringComparison.Ordinal);
        var compare = xaml.IndexOf("x:Name=\"HeaderCompareBars\"", StringComparison.Ordinal);
        Assert.True(transfer >= 0 && compare > transfer);
        Assert.Contains("Value=\"{Binding JobPercent, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding OverallPercent, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparePanelBindsProgressOneWay()
    {
        var xaml = File.ReadAllText(FindRepoFile(Path.Combine("src", "Mercury", "ComparePanel.xaml")));
        Assert.Contains("Value=\"{Binding ProgressValue, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Maximum=\"{Binding ProgressMaximum, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsIndeterminate=\"{Binding ProgressIndeterminate, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void QueueStoreRoundTripsCompareJob()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-compare-q-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var job = new Job
            {
                Kind = JobKind.Compare,
                Name = "Compare photos",
                SourcePath = Path.Combine(root, "left"),
                DestinationPath = Path.Combine(root, "right"),
                Options = new JobOptions
                {
                    CompareAdvanced = true,
                    CompareHash = true,
                    FatTimestampTolerance = true,
                    HashSourceForCompare = true
                }
            };
            QueueStore.Save(paths, [job]);
            var loaded = Assert.Single(QueueStore.Load(paths));
            Assert.Equal(JobKind.Compare, loaded.Kind);
            Assert.Equal("Compare photos", loaded.Name);
            Assert.True(loaded.Options.CompareAdvanced);
            Assert.True(loaded.Options.CompareHash);
            Assert.True(loaded.Options.FatTimestampTolerance);
            Assert.True(loaded.Options.HashSourceForCompare);
            var cloned = loaded.Options.Clone();
            Assert.True(cloned.HashSourceForCompare);
            Assert.False(new JobOptions().HashSourceForCompare);
            Assert.Equal(JobKind.Copy, new Job().Kind);

            var badges = JobOptionBadges.For(loaded);
            Assert.Equal(["Compare", "Advanced", "Hash", "Hash source", "FAT 2s"], badges);
            Assert.DoesNotContain("Unlimited speed", badges);
            Assert.Contains("Hash source", JobOptionBadges.For(new Job { Options = new JobOptions { HashSourceForCompare = true } }));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SourceHashesAreReusedByLaterCompare()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-src-hash-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        try
        {
            File.WriteAllText(Path.Combine(left, "a.txt"), "AAAA");
            File.WriteAllText(Path.Combine(right, "a.txt"), "AAAA");
            File.WriteAllText(Path.Combine(left, "b.txt"), "BBBB");
            File.WriteAllText(Path.Combine(right, "b.txt"), "BBBB");
            var jobs = Path.Combine(root, "jobs");
            var copyDir = Path.Combine(jobs, "copy1");
            Directory.CreateDirectory(copyDir);
            var sourceManifest = CompareManifestStore.JobFile(copyDir);
            DirectoryComparer.RecordSourceHashes(left, right, sourceManifest, fatTimestampTolerance: false);
            var saved = CompareManifestStore.Load(sourceManifest);
            Assert.NotNull(saved);
            Assert.True(saved!.SourceInventoryComplete);
            Assert.False(saved.InventoryComplete);
            Assert.Equal(2, saved.SourceHashCount);
            Assert.Equal(0, saved.DestHashCount);

            var again = new FileInfo(sourceManifest).Length;
            DirectoryComparer.RecordSourceHashes(left, right, sourceManifest, fatTimestampTolerance: false);
            Assert.Equal(again, new FileInfo(sourceManifest).Length);

            var starts = new List<string>();
            var compareManifest = Path.Combine(root, "compare-manifest.jsonl");
            var result = DirectoryComparer.Compare(
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
                manifestPath: compareManifest,
                seedJobsRoot: jobs);
            Assert.True(result.Completed);
            Assert.Equal(0, result.HashMismatches);
            Assert.Equal(["a.txt", "b.txt"], starts);
            var finished = CompareManifestStore.Load(compareManifest);
            Assert.Equal(2, finished!.SourceHashCount);
            Assert.Equal(2, finished.DestHashCount);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task CompareJobSurvivesRestartAndResumeSkipsSavedSourceHash()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-compare-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var left = Path.Combine(root, "left");
            var right = Path.Combine(root, "right");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            File.WriteAllText(Path.Combine(left, "a.txt"), "AAAA");
            File.WriteAllText(Path.Combine(right, "a.txt"), "AAAA");
            File.WriteAllText(Path.Combine(left, "b.txt"), "BBBB");
            File.WriteAllText(Path.Combine(right, "b.txt"), "BBBB");

            string jobId;
            using (var scheduler = new JobScheduler(paths))
            {
                var job = new Job
                {
                    Kind = JobKind.Compare,
                    Name = "Compare left",
                    SourcePath = left,
                    DestinationPath = right,
                    SourceKind = SourceKind.Folder,
                    Options = new JobOptions
                    {
                        CompareAdvanced = true,
                        CompareHash = true
                    }
                };
                jobId = job.Id;
                var stopped = 0;
                var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                scheduler.CompareActivity += (_, activity) =>
                {
                    if (activity.Result is { Canceled: true })
                    {
                        canceled.TrySetResult();
                    }

                    var pace = activity.Progress?.Pace;
                    if (activity.Progress?.CurrentRelative == "a.txt"
                        && pace is { FileBytesDone: > 0, FileBytesTotal: > 0 }
                        && pace.FileBytesDone == pace.FileBytesTotal
                        && Interlocked.Exchange(ref stopped, 1) == 0)
                    {
                        scheduler.Stop(activity.JobId);
                    }
                };
                scheduler.Enqueue(job, startNow: true);
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await WaitUntil(() =>
                {
                    var stored = QueueStore.Load(paths);
                    return !scheduler.HasRunningCompare
                        && stored.Any(row => row.Id == jobId && row.Status == JobStatus.Cancelled);
                }, TimeSpan.FromSeconds(20));
            }

            var manifest = CompareManifestStore.JobFile(paths.JobDirectory(jobId));
            var partial = CompareManifestStore.Load(manifest);
            Assert.NotNull(partial);
            Assert.True(partial!.InventoryComplete);
            Assert.Contains(partial.Files, file => file.Left && file.Relative == "a.txt" && !string.IsNullOrEmpty(file.ContentHash));
            Assert.DoesNotContain(partial.Files, file => file.Relative == "b.txt" && !string.IsNullOrEmpty(file.ContentHash));

            using (var again = new JobScheduler(paths))
            {
                var queued = Assert.Single(again.Queue);
                Assert.Equal(jobId, queued.Id);
                Assert.Equal(JobKind.Compare, queued.Kind);
                Assert.True(queued.Options.CompareAdvanced);
                Assert.True(queued.Options.CompareHash);
                Assert.NotEqual(JobStatus.Completed, queued.Status);
                again.Kick();
                again.KickCompare();
                await Task.Delay(200);
                Assert.False(again.HasRunningCompare);

                var starts = new List<string>();
                var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                again.CompareActivity += (_, activity) =>
                {
                    if (activity.Progress?.Pace is { FileBytesDone: 0, FileBytesTotal: > 0 })
                    {
                        lock (starts)
                        {
                            starts.Add(activity.Progress.CurrentRelative ?? "");
                        }
                    }

                    if (activity.Result is { Completed: true })
                    {
                        finished.TrySetResult();
                    }
                };
                again.ResumeOrRetry(jobId);
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
                await WaitUntil(() => !again.HasRunningCompare, TimeSpan.FromSeconds(20));
                Assert.Equal(["b.txt", "a.txt", "b.txt"], starts);
                Assert.Equal(JobStatus.Completed, Assert.Single(again.Queue).Status);
            }

            Assert.False(File.Exists(manifest));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void HashSourceCheckboxWritesSourceHashesWithoutDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-hash-source-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        try
        {
            File.WriteAllText(Path.Combine(left, "a.txt"), "AAAA");
            File.WriteAllText(Path.Combine(right, "a.txt"), "AAAA");
            File.WriteAllText(Path.Combine(left, "b.txt"), "BBBB");
            File.WriteAllText(Path.Combine(right, "b.txt"), "BBBB");
            var manifest = Path.Combine(root, "compare-manifest.jsonl");
            var sourceOnly = DirectoryComparer.Compare(
                left,
                right,
                new DirectoryCompareOptions { Advanced = true, HashSource = true },
                manifestPath: manifest);
            Assert.True(sourceOnly.Completed);
            Assert.False(sourceOnly.Hashed);
            var saved = CompareManifestStore.Load(manifest);
            Assert.NotNull(saved);
            Assert.True(saved!.InventoryComplete);
            Assert.Equal(2, saved.SourceHashCount);
            Assert.Equal(0, saved.DestHashCount);

            var starts = new List<string>();
            var hashed = DirectoryComparer.Compare(
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
            Assert.True(hashed.Completed);
            Assert.Equal(0, hashed.HashMismatches);
            Assert.Equal(["a.txt", "b.txt"], starts);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task CompareRunsBesideTransferAndStopDoesNotCancelTheOther()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-lanes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            var left = Path.Combine(root, "left");
            var right = Path.Combine(root, "right");
            var src = Path.Combine(root, "src");
            var dest = Path.Combine(root, "dest");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dest);
            for (var i = 0; i < 60; i++)
            {
                File.WriteAllText(Path.Combine(left, "f" + i.ToString("00") + ".txt"), new string('x', 1024));
                File.WriteAllText(Path.Combine(right, "f" + i.ToString("00") + ".txt"), new string('x', 1024));
            }

            File.WriteAllText(Path.Combine(src, "a.txt"), "a");
            var engine = new HoldingEngine();
            using var scheduler = new JobScheduler(paths, engine, new QuietRundown());
            var compare = new Job
            {
                Kind = JobKind.Compare,
                Name = "Compare left",
                SourcePath = left,
                DestinationPath = right,
                Options = new JobOptions { CompareAdvanced = true, CompareHash = true }
            };
            var transfer = new Job
            {
                Name = "copy",
                SourcePath = src,
                DestinationPath = dest
            };
            var comparePaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scheduler.CompareActivity += (_, activity) =>
            {
                if (activity.Progress is { FilesVisited: > 0 } && comparePaused.TrySetResult())
                {
                    scheduler.Pause(compare.Id);
                }
            };
            scheduler.Enqueue(compare, startNow: true);
            await comparePaused.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await WaitUntil(() => scheduler.HasRunningCompare, TimeSpan.FromSeconds(10));

            scheduler.Enqueue(transfer, startNow: true);
            await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(scheduler.HasRunningJob);
            Assert.True(scheduler.HasRunningCompare);
            Assert.Contains(transfer.Id, scheduler.RunningJobIds);
            Assert.DoesNotContain(compare.Id, scheduler.RunningJobIds);

            scheduler.Stop(transfer.Id);
            await WaitUntil(() => !scheduler.HasRunningJob, TimeSpan.FromSeconds(20));
            Assert.True(scheduler.HasRunningCompare);

            scheduler.Stop(compare.Id);
            await WaitUntil(() => !scheduler.HasRunningCompare, TimeSpan.FromSeconds(20));
            Assert.False(engine.Release.Task.IsCompleted);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private sealed class HoldingEngine : ICopyEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(
            Job job,
            JobJournal journal,
            BandwidthBudget budget,
            PauseGate pause,
            IJobLog log,
            IProgress<JobProgress>? progress,
            CancellationToken cancellationToken)
        {
            job.Status = JobStatus.Copying;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            job.Status = JobStatus.Completed;
        }
    }

    private sealed class QuietRundown : IRundownCapture
    {
        public void Capture(
            Job job,
            JobJournal journal,
            CopyMapping? mapping,
            IJobLog? log,
            string name,
            CancellationToken cancellationToken,
            Action<RundownProgress>? progress)
        {
        }
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(relative);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("condition not met");
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
        catch
        {
            // sqlite or a log file can still be closing
        }
    }
}
