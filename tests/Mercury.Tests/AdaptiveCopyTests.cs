using System.Collections.Concurrent;

namespace Mercury.Tests;

public class AdaptiveCopyTests
{
    [Fact]
    public void ProbeKeepsFourWideWhenItIsFaster()
    {
        var chosen = AdaptiveCopyPolicy.Choose(new Dictionary<AdaptiveCopyMode, double>
        {
            [AdaptiveCopyMode.Sequential] = 100,
            [AdaptiveCopyMode.Files2] = 110,
            [AdaptiveCopyMode.Files4] = 140
        });
        Assert.Equal(AdaptiveCopyMode.Files4, chosen);
    }

    [Fact]
    public void ProbeStaysOnOneStreamWhenParallelIsNotFaster()
    {
        var chosen = AdaptiveCopyPolicy.Choose(new Dictionary<AdaptiveCopyMode, double>
        {
            [AdaptiveCopyMode.Sequential] = 100,
            [AdaptiveCopyMode.Files2] = 110,
            [AdaptiveCopyMode.Files4] = 114,
            [AdaptiveCopyMode.Ranges2] = 90
        });
        Assert.Equal(AdaptiveCopyMode.Sequential, chosen);
    }

    [Fact]
    public void SpeedCapForcesOneStream()
    {
        var job = new Job { Options = new JobOptions { MaxMegabytesPerSecond = 5 } };
        var budget = new BandwidthBudget();
        Assert.True(AdaptiveCopyPolicy.IsCapped(job, budget));

        job.Options.MaxMegabytesPerSecond = null;
        Assert.False(AdaptiveCopyPolicy.IsCapped(job, budget));
        budget.GlobalMaxBytesPerSecond = 1024 * 1024;
        Assert.True(AdaptiveCopyPolicy.IsCapped(job, budget));

        var plan = TransferBalancer.Next(new BalanceInput
        {
            Mode = AdaptiveCopyMode.Files4,
            Capped = true,
            PendingFiles = 8,
            PendingCopyBytes = 80_000,
            LargestPendingBytes = 10_000,
            UnverifiedCopied = 3
        });
        Assert.Equal(1, plan.CopyWidth);
        Assert.False(plan.Stripe);
        Assert.Equal(3, plan.VerifyBatch);
    }

    [Fact]
    public void CandidatesCoverParallelFilesAndASplitLargeFile()
    {
        var files = Enumerable.Range(0, 4).Select(i => new FileRecord { RelativePath = i + ".bin", Size = 100 }).ToList();
        files[0].Size = AdaptiveCopyPolicy.LargeFileBytes;
        var modes = AdaptiveCopyPolicy.Candidates(files);
        Assert.Contains(AdaptiveCopyMode.Files2, modes);
        Assert.Contains(AdaptiveCopyMode.Files4, modes);
        Assert.Contains(AdaptiveCopyMode.Ranges2, modes);
    }

    [Fact]
    public void PackWaitsWhenSeveralFilesAtOnceWon()
    {
        var plan = TransferBalancer.Next(new BalanceInput
        {
            Mode = AdaptiveCopyMode.Files4,
            PendingFiles = 6,
            PendingCopyBytes = 6_000,
            PackBytesRemaining = 50_000,
            LargestPendingBytes = 1_000
        });
        Assert.False(plan.RunPack);
        Assert.Equal(4, plan.CopyWidth);
        Assert.Equal("Copying 4 files", plan.Label);
    }

    [Fact]
    public void LargeFileIsPortionedWhilePackingWhenThreadsDidNotWin()
    {
        var plan = TransferBalancer.Next(new BalanceInput
        {
            Mode = AdaptiveCopyMode.Sequential,
            PendingFiles = 1,
            PendingCopyBytes = AdaptiveCopyPolicy.LargeFileBytes,
            LargestPendingBytes = AdaptiveCopyPolicy.LargeFileBytes,
            PackBytesRemaining = 20_000_000
        });
        Assert.True(plan.RunPack);
        Assert.Equal(1, plan.CopyWidth);
        Assert.Equal(AdaptiveCopyPolicy.PortionBytes, plan.PortionBytes);
        Assert.Equal("Copying and packing", plan.Label);
    }

    [Fact]
    public void TwoRangesLabelWhenSplitWins()
    {
        var plan = TransferBalancer.Next(new BalanceInput
        {
            Mode = AdaptiveCopyMode.Ranges2,
            PendingFiles = 1,
            PendingCopyBytes = AdaptiveCopyPolicy.LargeFileBytes,
            LargestPendingBytes = AdaptiveCopyPolicy.LargeFileBytes
        });
        Assert.True(plan.Stripe);
        Assert.Equal(1, plan.CopyWidth);
        Assert.Null(plan.PortionBytes);
        Assert.Equal("Copying 2 ranges", plan.Label);
    }

    [Fact]
    public void RememberedModeIsPerVolumePair()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-adapt-mem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            AdaptiveCopyMemory.Use(root);
            AdaptiveCopyMemory.Remember(@"D:\photos\job", @"E:\archive\in", AdaptiveCopyMode.Files4, 12_000_000);
            Assert.True(AdaptiveCopyMemory.TryLookup(@"D:\other", @"E:\elsewhere", out var mode, out var bps));
            Assert.Equal(AdaptiveCopyMode.Files4, mode);
            Assert.Equal(12_000_000, bps);
            Assert.False(AdaptiveCopyMemory.TryLookup(@"D:\other", @"F:\elsewhere", out _, out _));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AdaptiveCopyDefaultsOnAndCopies()
    {
        Assert.True(new JobOptions().AdaptiveCopy);
        var copy = new JobOptions { AdaptiveCopy = false }.Clone();
        Assert.False(copy.AdaptiveCopy);
        Assert.Contains("One stream", JobOptionBadges.For(new Job { Options = copy }));
        Assert.DoesNotContain("One stream", JobOptionBadges.For(new Job()));
    }

    [Fact]
    public void PrimaryFileIsTheOneWithMostBytesLeft()
    {
        var pause = new PauseGate();
        pause.BeginFile("small", 10);
        pause.AddFileBytes(9, "small");
        pause.BeginFile("big", 1000);
        pause.AddFileBytes(10, "big");
        Assert.Equal("big", pause.CurrentFilePath);
        Assert.Equal(10, pause.CurrentFileCopied);
        pause.EndFile("big");
        Assert.Equal("small", pause.CurrentFilePath);
        Assert.Equal(1, pause.InFlightCount);
    }

    [Fact]
    public async Task TwoWorkersDoNotDoubleMarkAJournalRow()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-adapt-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var job = new Job
            {
                Name = "parallel-mark",
                SourcePath = root,
                DestinationPath = root
            };
            using var journal = JobJournal.Create(Path.Combine(root, "job"), job);
            var names = Enumerable.Range(0, 8).Select(i => $"f{i}.bin").ToList();
            foreach (var name in names)
            {
                journal.UpsertFile(new FileRecord
                {
                    RelativePath = name,
                    SourcePath = Path.Combine(root, name),
                    DestPath = Path.Combine(root, "out", name),
                    Size = 4,
                    LastWriteUtc = DateTime.UtcNow
                });
            }

            var hits = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            await AdaptiveCopyRunner.RunAsync(names, 4, (path, _) =>
            {
                hits.AddOrUpdate(path, 1, (_, n) => n + 1);
                journal.MarkCopied(path, "abc");
                return Task.CompletedTask;
            }, new PauseGate(), CancellationToken.None);

            Assert.Equal(8, hits.Count);
            Assert.All(hits.Values, count => Assert.Equal(1, count));
            var stored = journal.GetFiles();
            Assert.Equal(8, stored.Count);
            Assert.All(stored, file =>
            {
                Assert.Equal(FileCopyStatus.Copied, file.Status);
                Assert.Equal("abc", file.Hash);
            });
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PauseAfterStopsNewFilesAndPauseHoldsTheCopy()
    {
        var pause = new PauseGate();
        pause.RequestPauseAfterFile();
        var started = 0;
        await AdaptiveCopyRunner.RunAsync(
            ["a", "b", "c", "d"],
            2,
            (_, _) =>
            {
                Interlocked.Increment(ref started);
                return Task.CompletedTask;
            },
            pause,
            CancellationToken.None);
        Assert.Equal(0, started);
        Assert.True(pause.IsPaused);

        var gate = new PauseGate();
        gate.Pause();
        var entered = 0;
        var running = AdaptiveCopyRunner.RunAsync(
            ["a", "b"],
            1,
            async (_, token) =>
            {
                Interlocked.Increment(ref entered);
                await gate.WaitIfPausedAsync(token);
                Interlocked.Increment(ref entered);
            },
            gate,
            CancellationToken.None);
        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref entered));
        gate.Resume();
        await running;
        Assert.Equal(4, Volatile.Read(ref entered));
    }

    [Fact]
    public async Task SplitResumeCompletesTheUnfinishedRangeWithoutRecopyingTheFinishedOne()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-stripe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var src = Path.Combine(root, "big.bin");
            var dest = Path.Combine(root, "out", "big.bin");
            var bytes = new byte[256 * 1024];
            Random.Shared.NextBytes(bytes);
            bytes[0] = 0x11;
            File.WriteAllBytes(src, bytes);
            var job = new Job
            {
                Name = "stripe",
                SourcePath = root,
                DestinationPath = Path.Combine(root, "out"),
                Options = new JobOptions { RetryCount = 0 }
            };
            var file = new FileRecord
            {
                RelativePath = "big.bin",
                SourcePath = src,
                DestPath = dest,
                Size = bytes.Length,
                LastWriteUtc = File.GetLastWriteTimeUtc(src)
            };
            var budget = new BandwidthBudget();
            var first = await FileCopier.CopyAdaptiveAsync(
                job, file, budget, new PauseGate(), new SpeedTracker(), CancellationToken.None, null, job.Name, null,
                stripe: true, maxNewBytes: bytes.Length / 2);
            Assert.False(first.Finished);
            Assert.False(first.Fallback);
            Assert.False(File.Exists(dest));
            var part0 = dest + ".mercury.r0.tmp";
            Assert.Equal(bytes.Length / 2, new FileInfo(part0).Length);
            Assert.False(File.Exists(dest + ".mercury.r1.tmp"));

            var corrupt = File.ReadAllBytes(part0);
            corrupt[0] = 0xEE;
            File.WriteAllBytes(part0, corrupt);

            var second = await FileCopier.CopyAdaptiveAsync(
                job, file, budget, new PauseGate(), new SpeedTracker(), CancellationToken.None, null, job.Name, null,
                stripe: true);
            Assert.True(second.Finished);
            var done = File.ReadAllBytes(dest);
            Assert.Equal(bytes.Length, done.Length);
            Assert.Equal(0xEE, done[0]);
            Assert.Equal(bytes.AsSpan(bytes.Length / 2).ToArray(), done.AsSpan(bytes.Length / 2).ToArray());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HighProbeBaselineDoesNotForceOneStreamWhenSustainedRateIsTheNetwork()
    {
        const double mb = 1024d * 1024d;
        var oneStream = 84.9 * mb;
        var samples = new[]
        {
            new ProbeSample(AdaptiveCopyMode.Sequential, (long)(96 * mb), 96 / 84.9),
            new ProbeSample(AdaptiveCopyMode.Files2, (long)(96 * mb), 1.1)
        };
        Assert.False(AdaptiveCopyPolicy.TrustProbeBaseline(3.0, oneStream, samples));

        var tracker = new AdaptiveSustainTracker();
        tracker.Start(0, 0);
        var network = 6 * mb;
        var early = tracker.Observe((long)(32 * mb), (32 * mb) / network, AdaptiveCopyMode.Files2);
        Assert.False(early.DropToOneStream);
        Assert.False(early.LockedBaseline);

        var chunk = AdaptiveCopyPolicy.SustainBytes;
        var locked = tracker.Observe(chunk, chunk / network, AdaptiveCopyMode.Files2);
        Assert.True(locked.LockedBaseline);
        Assert.False(locked.DropToOneStream);
        Assert.InRange(tracker.BaselineBps, network * 0.95, network * 1.05);

        var steady = tracker.Observe(chunk * 2, (chunk * 2) / network, AdaptiveCopyMode.Files2);
        Assert.False(steady.DropToOneStream);
        Assert.False(tracker.Dropped);
    }

    [Fact]
    public void ProbeBurstAboveALaterSampleIsNotTheBaseline()
    {
        const double mb = 1024d * 1024d;
        var samples = new[]
        {
            new ProbeSample(AdaptiveCopyMode.Sequential, (long)(80 * mb), 1),
            new ProbeSample(AdaptiveCopyMode.Files2, (long)(8 * mb), 1)
        };
        Assert.False(AdaptiveCopyPolicy.TrustProbeBaseline(6.5, 80 * mb, samples));
        Assert.True(AdaptiveCopyPolicy.TrustProbeBaseline(
            6.5,
            20 * mb,
            [new ProbeSample(AdaptiveCopyMode.Sequential, (long)(20 * mb), 1), new ProbeSample(AdaptiveCopyMode.Files2, (long)(24 * mb), 1)]));
    }

    [Fact]
    public void RealDropFromSustainedChosenModeForcesOneStream()
    {
        const double mb = 1024d * 1024d;
        var tracker = new AdaptiveSustainTracker();
        tracker.Start(0, 0);
        var chunk = AdaptiveCopyPolicy.SustainBytes;
        var fast = chunk / (6 * mb);
        tracker.Observe(chunk, fast, AdaptiveCopyMode.Files2);
        var slow = chunk / (3 * mb);
        var drop = tracker.Observe(chunk * 2, fast + slow, AdaptiveCopyMode.Files2);
        Assert.True(drop.DropToOneStream);
        Assert.True(tracker.Dropped);
        Assert.True(drop.LiveBps < tracker.BaselineBps * AdaptiveCopyPolicy.SpeedDropRatio);

        var line = AdaptiveCopyLog.WidthChange(
            AdaptiveCopyMode.Files2,
            AdaptiveCopyMode.Sequential,
            drop.LiveBps,
            drop.ComparisonBps,
            "parallel copy slowed down");
        Assert.Contains("2 files at once → one stream", line);
        Assert.Contains("live ", line);
        Assert.Contains(" vs ", line);
        Assert.Contains("parallel copy slowed down", line);
        Assert.Contains("MB/s", line);
    }

    [Fact]
    public void LabelsSayCopyingTwoFilesAndOneStreamAfterFalloff()
    {
        var two = TransferBalancer.Next(new BalanceInput
        {
            Mode = AdaptiveCopyMode.Files2,
            PendingFiles = 6,
            PendingCopyBytes = 200_000,
            LargestPendingBytes = 40_000
        });
        Assert.Equal("Copying 2 files", two.Label);
        Assert.Equal(AdaptiveCopyPolicy.CopyingTwoFiles, two.Label);

        var one = TransferBalancer.Next(new BalanceInput
        {
            Mode = AdaptiveCopyMode.Sequential,
            PendingFiles = 6,
            PendingCopyBytes = 200_000,
            LargestPendingBytes = 40_000
        });
        Assert.Equal("Copying one stream", one.Label);

        var fell = TransferBalancer.Next(new BalanceInput
        {
            Mode = AdaptiveCopyMode.Sequential,
            FellOff = true,
            PendingFiles = 6,
            PendingCopyBytes = 200_000,
            LargestPendingBytes = 40_000
        });
        Assert.Equal("One stream — parallel copy slowed down", fell.Label);
        Assert.Equal(AdaptiveCopyPolicy.OneStreamSlowedDown, fell.Label);
    }

    [Fact]
    public void ProbeLogListsRanModesAndSkipsTheRest()
    {
        const long bytes = 128L * 1024 * 1024;
        var samples = new[]
        {
            new ProbeSample(AdaptiveCopyMode.Sequential, bytes, 1.5),
            new ProbeSample(AdaptiveCopyMode.Files2, bytes, 1.2)
        };
        var line = AdaptiveCopyLog.ProbeLine(samples, 2.7, AdaptiveCopyMode.Files2);
        Assert.Contains("one stream", line);
        Assert.Contains("128 MB", line);
        Assert.Contains("1.5s", line);
        Assert.Contains("2 files", line);
        Assert.Contains("1.2s", line);
        Assert.Contains("MB/s", line);
        Assert.Contains("Chose 2 files at once", line);
        Assert.Contains("15% faster than one stream", line);
        Assert.Contains("Probe 2.7s is not the baseline", line);
        Assert.DoesNotContain("4 files", line);
        Assert.DoesNotContain("2 ranges", line);

        var beat = AdaptiveCopyLog.Heartbeat(
            "Copying 2 files",
            [@"Warren Truss\compressed\a.zip", @"Warren Truss\compressed\b.zip"],
            6.2 * 1024 * 1024,
            6.0 * 1024 * 1024);
        Assert.Contains("Copying 2 files", beat);
        Assert.Contains(@"Warren Truss\compressed\a.zip", beat);
        Assert.Contains(@"Warren Truss\compressed\b.zip", beat);
        Assert.Contains("window ", beat);
        Assert.Contains("job ", beat);
        Assert.Contains("MB/s", beat);
    }

    [Fact]
    public void InFlightLabelNamesThePrimaryAndTheOtherFiles()
    {
        Assert.Equal("a.zip", AdaptiveCopyPolicy.WithOtherFiles("a.zip", 0));
        Assert.Equal("a.zip and 1 other file", AdaptiveCopyPolicy.WithOtherFiles("a.zip", 1));
        Assert.Equal("a.zip and 3 other files", AdaptiveCopyPolicy.WithOtherFiles("a.zip", 3));

        var pause = new PauseGate();
        pause.BeginFile("small.zip", 10);
        pause.AddFileBytes(9, "small.zip");
        pause.BeginFile("big.zip", 1000);
        pause.AddFileBytes(10, "big.zip");
        var paths = pause.InFlightPaths();
        Assert.Equal("big.zip", paths[0]);
        Assert.Equal(2, paths.Count);
        Assert.Contains("small.zip", paths);
    }

    [Fact]
    public void GapOrProbeBytesDoNotCollapseTheDisplayedRate()
    {
        const double mb = 1024d * 1024d;
        long ticks = 0;
        var tracker = new SpeedTracker();
        tracker.SetClock(() => ticks);
        void Advance(double seconds) => ticks += (long)(seconds * System.Diagnostics.Stopwatch.Frequency);

        tracker.BeginStage();
        Advance(1);
        tracker.Add((long)(100 * mb));
        tracker.BeginStage();
        Advance(5);
        tracker.Add((long)(30 * mb));
        var afterProbe = tracker.EffectiveBytesPerSecond;
        Assert.InRange(afterProbe, 5.5 * mb, 6.5 * mb);

        Advance(3);
        tracker.Add((long)(18 * mb));
        var steady = tracker.EffectiveBytesPerSecond;
        Assert.InRange(steady, 5 * mb, 7 * mb);

        Advance(8);
        tracker.Add((long)(1 * mb));
        var afterGap = tracker.EffectiveBytesPerSecond;
        Assert.True(afterGap > 2 * mb);
        Assert.True(afterGap > (1 * mb) / 8d * 5);

        Assert.Equal(6 * mb, SpeedTracker.ChooseDisplayedRate(6 * mb, (long)(48 * mb), 6 * mb, 6 * mb), 1);
        var thin = SpeedTracker.ChooseDisplayedRate((1 * mb) / 8d, (long)(1 * mb), 6 * mb, 6 * mb);
        Assert.Equal(6 * mb, thin, 1);
    }

    [Fact]
    public void MeasuredStageRateIsNotReplacedByTheWholeJobAverage()
    {
        var now = new DateTimeOffset(2026, 9, 29, 18, 16, 0, TimeSpan.Zero);
        var quiet = ProgressStats.From(new JobProgress
        {
            Status = JobStatus.Copying,
            SpeedMeasured = true,
            BytesPerSecond = 0,
            BytesCopied = 4_000_000_000_000,
            BytesTotal = 4_500_000_000_000,
            StartedUtc = now.AddHours(-10),
            StageStartedUtc = now.AddMinutes(-4),
            StageName = "Copying",
            StageIndex = 2,
            StageCount = 4,
            Message = "Testing copy speed…"
        }, now);
        Assert.Equal("—", quiet.Speed.Value);

        var rate = 6d * 1024 * 1024;
        var copying = ProgressStats.From(new JobProgress
        {
            Status = JobStatus.Copying,
            SpeedMeasured = true,
            BytesPerSecond = rate,
            BytesCopied = 4_000_000_000_000,
            BytesTotal = 4_500_000_000_000,
            StartedUtc = now.AddHours(-10),
            StageStartedUtc = now.AddMinutes(-4),
            StageName = "Copying",
            StageIndex = 2,
            StageCount = 4,
            Message = "Copying 2 files",
            CurrentFile = @"Warren Truss\compressed\a.zip and 1 other file"
        }, now);
        Assert.Equal(ByteFormatter.Speed(rate), copying.Speed.Value);
        Assert.NotEqual(ByteFormatter.Speed(4_000_000_000_000d / TimeSpan.FromHours(10).TotalSeconds), copying.Speed.Value);
        Assert.Equal(@"Warren Truss\compressed\a.zip and 1 other file", copying.CurrentFileBarText);
        Assert.Equal("—", copying.ThisFile.Value);
    }
}
