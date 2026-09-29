namespace Mercury.Tests;

public class CompareQueueTests
{
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
            Assert.Equal(["Compare", "Advanced", "Hash", "FAT 2s"], badges);
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
                    return !scheduler.HasRunningJob
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
                await Task.Delay(200);
                Assert.False(again.HasRunningJob);

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
                await WaitUntil(() => !again.HasRunningJob, TimeSpan.FromSeconds(20));
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
