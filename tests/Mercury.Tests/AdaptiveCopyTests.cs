using System.Collections.Concurrent;
using System.Text;

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
        Assert.DoesNotContain(AdaptiveCopyMode.Files8, modes);
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

        Assert.StartsWith("Adaptive copy probe:", line);

        var beat = AdaptiveCopyLog.Heartbeat(
            "Copying 2 files",
            [@"Warren Truss\compressed\a.zip", @"Warren Truss\compressed\b.zip"],
            6.2 * 1024 * 1024,
            6.0 * 1024 * 1024);
        Assert.Equal(30, AdaptiveCopyPolicy.HeartbeatSeconds);
        Assert.StartsWith("2 files, ", beat);
        Assert.DoesNotContain("Adaptive copy", beat);
        Assert.Contains(@"Warren Truss\compressed\a.zip", beat);
        Assert.Contains(@"Warren Truss\compressed\b.zip", beat);
        Assert.Contains("window ", beat);
        Assert.Contains("job ", beat);
        Assert.Contains("MB/s", beat);

        var detailed = AdaptiveCopyLog.Heartbeat(
            "Copying 2 files — adaptive → 2 files, adaptive turned off",
            [@"Warren Truss\compressed\a.zip"],
            10.1 * 1024 * 1024,
            11.5 * 1024 * 1024);
        Assert.StartsWith("2 files, ", detailed);
        Assert.Contains("window 10.1 MB/s", detailed);
        Assert.Contains("job 11.5 MB/s", detailed);
        Assert.DoesNotContain("Adaptive copy", detailed);
    }

    [Fact]
    public void CopyModeReadoutNamesTheModeInUseAndIgnoresOtherProgressText()
    {
        Assert.Equal("Copy mode", CopyModeLabels.Label);
        Assert.Equal("Testing", CopyModeLabels.FromMessage(AdaptiveCopyPolicy.TestingStatus));
        Assert.Equal("2 files", CopyModeLabels.FromMessage("Copying 2 files"));
        Assert.Equal("4 files", CopyModeLabels.FromMessage("Copying 4 files — adaptive → 4 files, adaptive turned off"));
        Assert.Equal("8 files", CopyModeLabels.FromMessage(AdaptiveCopyPolicy.CopyingEightFiles));
        Assert.Equal("2 ranges", CopyModeLabels.FromMessage("Copying 2 ranges and packing"));
        Assert.Equal("One stream", CopyModeLabels.FromMessage(AdaptiveCopyPolicy.CopyingOneStream));
        Assert.Equal("One stream", CopyModeLabels.FromMessage(AdaptiveCopyPolicy.OneStreamSlowedDown));
        Assert.Equal("One stream", CopyModeLabels.FromMessage("One stream — clip.mkv is too small to split"));
        Assert.Null(CopyModeLabels.FromMessage(@"Copying Warren Truss\compressed\a.zip"));
        Assert.Null(CopyModeLabels.FromMessage("Copy mode: adaptive → 2 files, adaptive turned off."));
        Assert.Null(CopyModeLabels.FromMessage("Paused — outside hours"));
        Assert.Null(CopyModeLabels.FromMessage(null));
        Assert.Equal("2 files", CopyModeLabels.ForHeader(HeaderCopyMode.Files2));
        Assert.Equal("4 files", CopyModeLabels.ForHeader(HeaderCopyMode.Files4));
        Assert.Equal("8 files", CopyModeLabels.ForHeader(HeaderCopyMode.Files8));
        Assert.Equal("2 ranges", CopyModeLabels.ForHeader(HeaderCopyMode.Ranges2));
        Assert.Equal("One stream", CopyModeLabels.ForHeader(HeaderCopyMode.OneStream));
        Assert.Null(CopyModeLabels.ForHeader(HeaderCopyMode.Adaptive));
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

    [Fact]
    public void ManualModeForcesThatWidthAndDoesNotProbe()
    {
        var selection = new HeaderCopySelection();
        var line = selection.Select(HeaderCopyMode.Files2);
        Assert.False(HeaderCopy.ShouldProbe(selection.Mode, savedAdaptive: true, pendingLargeEnough: true, remembered: false));
        Assert.Equal(AdaptiveCopyMode.Files2, HeaderCopy.EngineMode(selection.Mode));
        Assert.Equal(2, AdaptiveCopyPolicy.Width(HeaderCopy.EngineMode(selection.Mode)));
        Assert.Equal(4, AdaptiveCopyPolicy.Width(HeaderCopy.EngineMode(HeaderCopyMode.Files4)));
        Assert.Equal(AdaptiveCopyMode.Ranges2, HeaderCopy.EngineMode(HeaderCopyMode.Ranges2));
        Assert.Equal(AdaptiveCopyMode.Sequential, HeaderCopy.EngineMode(HeaderCopyMode.OneStream));
        Assert.NotNull(line);
        Assert.Contains("adaptive → 2 files", line);
        Assert.Contains("adaptive turned off", line);

        selection.Select(HeaderCopyMode.Files4);
        Assert.False(HeaderCopy.ShouldProbe(selection.Mode, savedAdaptive: true, pendingLargeEnough: true, remembered: false));
        Assert.Equal(4, AdaptiveCopyPolicy.Width(HeaderCopy.EngineMode(selection.Mode)));
    }

    [Fact]
    public void ChoosingAdaptiveAgainAllowsProbeOrRememberedPath()
    {
        var selection = new HeaderCopySelection();
        selection.Select(HeaderCopyMode.OneStream);
        var back = selection.Select(HeaderCopyMode.Adaptive);
        Assert.True(selection.Adaptive);
        Assert.False(selection.OneStream);
        Assert.NotNull(back);
        Assert.Contains("one stream → adaptive", back);
        Assert.DoesNotContain("turned off", back);
        Assert.True(HeaderCopy.ShouldProbe(selection.Mode, savedAdaptive: true, pendingLargeEnough: true, remembered: false));
        Assert.False(HeaderCopy.ShouldProbe(selection.Mode, savedAdaptive: true, pendingLargeEnough: true, remembered: true));
        Assert.True(HeaderCopy.UsesAdaptive(HeaderCopyMode.Adaptive, savedAdaptive: false));
        Assert.True(HeaderCopy.UsesAdaptive(HeaderCopyMode.FollowSaved, savedAdaptive: true));
        Assert.False(HeaderCopy.UsesAdaptive(HeaderCopyMode.FollowSaved, savedAdaptive: false));
    }

    [Fact]
    public void OnlyOneHeaderCopyModeCanBeOn()
    {
        var selection = new HeaderCopySelection();
        Assert.True(selection.Adaptive);
        Assert.Equal(1, selection.OnCount);
        selection.Select(HeaderCopyMode.Files4);
        Assert.True(selection.Files4);
        Assert.False(selection.Adaptive);
        Assert.False(selection.OneStream);
        Assert.False(selection.Files2);
        Assert.False(selection.Files8);
        Assert.False(selection.Ranges2);
        Assert.Equal(1, selection.OnCount);
        selection.Select(HeaderCopyMode.Ranges2);
        Assert.True(selection.Ranges2);
        Assert.False(selection.Files4);
        Assert.Equal(1, selection.OnCount);
        Assert.Null(selection.Select(HeaderCopyMode.Ranges2));
        Assert.Equal(1, selection.OnCount);
    }

    [Fact]
    public void EightFilesIsAManualWidthAndNotAProbeCandidate()
    {
        var files = Enumerable.Range(0, 8).Select(i => new FileRecord
        {
            RelativePath = i + ".zip",
            Size = 33_000_000
        }).ToList();
        var modes = AdaptiveCopyPolicy.Candidates(files);
        Assert.DoesNotContain(AdaptiveCopyMode.Files8, modes);
        Assert.Contains(AdaptiveCopyMode.Files2, modes);
        Assert.Contains(AdaptiveCopyMode.Files4, modes);
        Assert.Equal(4, AdaptiveCopyPolicy.MaxWorkers);
        Assert.Equal(4, AdaptiveCopyPolicy.Width(AdaptiveCopyMode.Files4));
        Assert.Equal(8, AdaptiveCopyPolicy.Width(AdaptiveCopyMode.Files8));
        Assert.Equal(AdaptiveCopyPolicy.ManualEightWorkers, AdaptiveCopyPolicy.Width(HeaderCopy.EngineMode(HeaderCopyMode.Files8)));
        Assert.False(HeaderCopy.ShouldProbe(HeaderCopyMode.Files8, savedAdaptive: true, pendingLargeEnough: true, remembered: false));

        var selection = new HeaderCopySelection();
        var line = selection.Select(HeaderCopyMode.Files8);
        Assert.NotNull(line);
        Assert.Contains("adaptive → 8 files", line);
        Assert.Contains("adaptive turned off", line);
        Assert.Contains("8 files", line);
        Assert.True(selection.Files8);
        Assert.False(selection.Adaptive);
        Assert.False(selection.Files4);
        Assert.False(selection.Ranges2);
        Assert.Equal(1, selection.OnCount);

        var plan = TransferBalancer.Next(new BalanceInput
        {
            Mode = AdaptiveCopyMode.Files8,
            Manual = true,
            PendingFiles = 8,
            PendingCopyBytes = 8_000,
            LargestPendingBytes = 1_000
        });
        Assert.Equal(8, plan.CopyWidth);
        Assert.False(plan.Stripe);
        Assert.Equal("Copying 8 files", plan.Label);
        Assert.Equal(
            "Copying 8 files — adaptive → 8 files, adaptive turned off",
            HeaderCopy.RunningStatus(AdaptiveCopyMode.Files8, HeaderCopy.SwitchDetail(HeaderCopyMode.Adaptive, HeaderCopyMode.Files8)));
    }

    [Fact]
    public void TypedFileCountCopiesThatManyAndIsNotProbed()
    {
        Assert.False(AdaptiveCopyPolicy.TryParseManualFiles("", out _));
        Assert.False(AdaptiveCopyPolicy.TryParseManualFiles("0", out _));
        Assert.False(AdaptiveCopyPolicy.TryParseManualFiles("33", out _));
        Assert.False(AdaptiveCopyPolicy.TryParseManualFiles("12 files", out _));
        Assert.True(AdaptiveCopyPolicy.TryParseManualFiles("12", out var width));
        Assert.Equal(12, width);
        Assert.Equal(32, AdaptiveCopyPolicy.ManualFileMax);
        Assert.Equal(12, AdaptiveCopyPolicy.Width(AdaptiveCopyMode.FilesN, 12));
        Assert.DoesNotContain(AdaptiveCopyMode.FilesN, AdaptiveCopyPolicy.Candidates(
        [
            new FileRecord { RelativePath = "a.zip", Size = 33_000_000 },
            new FileRecord { RelativePath = "b.zip", Size = 33_000_000 },
            new FileRecord { RelativePath = "c.zip", Size = 33_000_000 },
            new FileRecord { RelativePath = "d.zip", Size = 33_000_000 }
        ]));
        Assert.False(HeaderCopy.ShouldProbe(HeaderCopyMode.FilesN, savedAdaptive: true, pendingLargeEnough: true, remembered: false));

        var selection = new HeaderCopySelection();
        var line = selection.Select(HeaderCopyMode.FilesN);
        Assert.NotNull(line);
        Assert.True(selection.FilesN);
        Assert.False(selection.Adaptive);
        Assert.False(selection.Files8);
        Assert.Equal(1, selection.OnCount);
        Assert.Equal(
            "Copy mode: adaptive → 12 files, adaptive turned off.",
            HeaderCopy.SwitchLine(HeaderCopyMode.Adaptive, HeaderCopyMode.FilesN, toWidth: 12));
        Assert.Equal(
            "Copy mode: 12 files → 16 files, adaptive turned off.",
            HeaderCopy.SwitchLine(HeaderCopyMode.FilesN, HeaderCopyMode.FilesN, toWidth: 16, fromWidth: 12));

        var plan = TransferBalancer.Next(new BalanceInput
        {
            Mode = AdaptiveCopyMode.FilesN,
            Manual = true,
            ManualWidth = 12,
            PendingFiles = 20,
            PendingCopyBytes = 20_000,
            LargestPendingBytes = 1_000
        });
        Assert.Equal(12, plan.CopyWidth);
        Assert.False(plan.Stripe);
        Assert.Equal("Copying 12 files", plan.Label);
        Assert.Equal("12 files", CopyModeLabels.FromMessage("Copying 12 files — adaptive → 12 files, adaptive turned off."));
        Assert.Equal(
            "Copying 12 files — adaptive → 12 files, adaptive turned off",
            HeaderCopy.RunningStatus(AdaptiveCopyMode.FilesN, HeaderCopy.SwitchDetail(HeaderCopyMode.Adaptive, HeaderCopyMode.FilesN, 12), fileWidth: 12));
    }

    [Fact]
    public void FileTooSmallToSplitStaysOnOneStreamAndSaysSo()
    {
        Assert.True(HeaderCopy.TooSmallToSplit(AdaptiveCopyPolicy.LargeFileBytes - 1));
        Assert.False(HeaderCopy.TooSmallToSplit(AdaptiveCopyPolicy.LargeFileBytes));
        var status = HeaderCopy.TooSmallStatus(@"Warren Truss\compressed\a.zip");
        var log = HeaderCopy.TooSmallLog(@"Warren Truss\compressed\a.zip");
        Assert.Contains("One stream", status);
        Assert.Contains("too small to split", status);
        Assert.Contains(@"Warren Truss\compressed\a.zip", status);
        Assert.Contains("2 ranges → one stream", log);
        Assert.Contains("too small to split", log);
    }

    [Fact]
    public void ManualChoiceDoesNotChangeTheSavedAdaptiveOption()
    {
        var job = new Job { Options = new JobOptions { AdaptiveCopy = true } };
        var selection = new HeaderCopySelection();
        selection.Select(HeaderCopyMode.Files2);
        job.HeaderCopyMode = selection.Mode;
        job.HeaderCopyModeChosen = selection.UserPicked;
        Assert.True(job.Options.AdaptiveCopy);
        Assert.False(HeaderCopy.ShouldProbe(job.HeaderCopyMode, job.Options.AdaptiveCopy, pendingLargeEnough: true, remembered: false));
        selection.Reset();
        Assert.True(selection.Adaptive);
        Assert.False(selection.UserPicked);
        Assert.True(job.Options.AdaptiveCopy);
    }

    [Fact]
    public async Task ProbeWritesANewTestNameAndDoesNotTouchFinalDestOrCopiedSources()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-probe-fresh-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var dest = Path.Combine(root, "dest");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(dest);
        FileStream? locked = null;
        try
        {
            var copiedSource = Path.Combine(source, "already-copied.bin");
            var copiedDest = Path.Combine(dest, "nested", "already-copied.bin");
            WriteFilled(copiedSource, 2 * 1024 * 1024, 0x11);
            Directory.CreateDirectory(Path.GetDirectoryName(copiedDest)!);
            File.WriteAllBytes(copiedDest, "COPIED"u8.ToArray());

            var pending = new (string Name, byte Fill, bool SeedDest)[]
            {
                ("pending-0.bin", 0x21, true),
                ("pending-1.bin", 0x22, false),
                ("sub/pending-2.bin", 0x23, true),
                ("pending-3.bin", 0x24, false)
            };
            var records = new List<FileRecord>();
            var job = new Job { Name = "probe-fresh", SourcePath = source, DestinationPath = dest };
            using var journal = JobJournal.Create(Path.Combine(root, "journal"), job);
            journal.UpsertFile(Record("already-copied.bin", copiedSource, copiedDest, new FileInfo(copiedSource).Length));
            journal.MarkCopied("already-copied.bin", "copied-hash");
            foreach (var item in pending)
            {
                var src = Path.Combine(source, item.Name.Replace('/', Path.DirectorySeparatorChar));
                var dst = Path.Combine(dest, item.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(src)!);
                WriteFilled(src, 400 * 1024, item.Fill);
                if (item.SeedDest)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.WriteAllBytes(dst, Encoding.ASCII.GetBytes("FINAL-" + item.Name));
                }

                journal.UpsertFile(Record(item.Name, src, dst, new FileInfo(src).Length));
            }

            var before = journal.GetFiles().OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
            Assert.Contains(before, f => f.RelativePath == "already-copied.bin" && f.Status == FileCopyStatus.Copied);
            Assert.Equal(4, before.Count(f => f.Status == FileCopyStatus.Pending));
            locked = new FileStream(copiedSource, FileMode.Open, FileAccess.Read, FileShare.None);

            var seen = new List<(string Path, byte First)>();
            var result = await AdaptiveCopyProbe.MeasureAsync(before, CancellationToken.None, path =>
            {
                var buffer = new byte[1];
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Read(buffer, 0, 1) == 1)
                {
                    seen.Add((path, buffer[0]));
                }
            });

            Assert.False(result.Failed);
            Assert.False(result.NoPending);
            Assert.NotEmpty(result.Samples);
            var line = AdaptiveCopyLog.ProbeLine(result.Samples, result.ElapsedSeconds, result.Mode);
            Assert.Contains(".mercury-speed-test.tmp.", line);
            Assert.Contains("MB/s", line);
            Assert.Contains(" in ", line);

            AssertSamplePaths(result, AdaptiveCopyMode.Sequential, 1, before);
            AssertSamplePaths(result, AdaptiveCopyMode.Files2, 2, before);
            AssertSamplePaths(result, AdaptiveCopyMode.Files4, 4, before);
            Assert.DoesNotContain(result.Samples, s => s.Mode == AdaptiveCopyMode.Ranges2);

            Assert.NotEmpty(seen);
            Assert.All(seen, hit =>
            {
                Assert.Contains(hit.First, new byte[] { 0x21, 0x22, 0x23, 0x24 });
                Assert.Contains(".mercury-speed-test.tmp.", hit.Path);
                Assert.DoesNotContain("already-copied.bin.mercury-speed-test.tmp.", hit.Path.Replace('\\', '/'));
            });
            Assert.Equal(seen.Count, seen.Select(h => h.Path).Distinct(StringComparer.Ordinal).Count());

            foreach (var file in before)
            {
                Assert.False(File.Exists(file.DestPath + ".mercury.tmp"));
                if (file.Status == FileCopyStatus.Copied)
                {
                    Assert.Equal("COPIED"u8.ToArray(), File.ReadAllBytes(file.DestPath));
                    continue;
                }

                var seeded = pending.First(p => p.Name == file.RelativePath);
                if (seeded.SeedDest)
                {
                    Assert.Equal(Encoding.ASCII.GetBytes("FINAL-" + seeded.Name), File.ReadAllBytes(file.DestPath));
                }
                else
                {
                    Assert.False(File.Exists(file.DestPath));
                }
            }

            Assert.DoesNotContain(
                Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories),
                path => path.Contains(".mercury-speed-test.tmp.", StringComparison.Ordinal));

            var after = journal.GetFiles().OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList();
            Assert.Equal(before.Count, after.Count);
            for (var i = 0; i < before.Count; i++)
            {
                Assert.Equal(before[i].RelativePath, after[i].RelativePath);
                Assert.Equal(before[i].Status, after[i].Status);
                Assert.Equal(before[i].BytesCopied, after[i].BytesCopied);
                Assert.Equal(before[i].Hash, after[i].Hash);
                Assert.Equal(before[i].DestPath, after[i].DestPath);
            }
        }
        finally
        {
            locked?.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProbeSkipsWhenEveryJournalFileIsAlreadyCopied()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-probe-skip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "done.bin");
            var dest = Path.Combine(root, "out", "done.bin");
            WriteFilled(source, 2 * 1024 * 1024, 0x44);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, "KEEP"u8.ToArray());
            var job = new Job { Name = "probe-skip", SourcePath = root, DestinationPath = Path.Combine(root, "out") };
            using var journal = JobJournal.Create(Path.Combine(root, "journal"), job);
            journal.UpsertFile(Record("done.bin", source, dest, new FileInfo(source).Length));
            journal.MarkCopied("done.bin", "h");
            var files = journal.GetFiles();
            var seen = 0;
            var result = await AdaptiveCopyProbe.MeasureAsync(files, CancellationToken.None, _ => seen++);
            Assert.True(result.NoPending);
            Assert.Empty(result.Samples);
            Assert.Equal(AdaptiveCopyMode.Sequential, result.Mode);
            Assert.False(result.Failed);
            Assert.Equal(0, seen);
            Assert.Equal("KEEP"u8.ToArray(), File.ReadAllBytes(dest));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(Path.GetDirectoryName(dest)!, "*", SearchOption.AllDirectories),
                path => path.Contains(".mercury-speed-test.tmp.", StringComparison.Ordinal));
            Assert.Equal(FileCopyStatus.Copied, journal.GetFiles().Single().Status);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProbeDeletesTheTestFileWhenTheSampleIsCancelled()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-probe-cancel-" + Guid.NewGuid().ToString("N"));
        var destDir = Path.Combine(root, "dest");
        Directory.CreateDirectory(destDir);
        try
        {
            var source = Path.Combine(root, "big.bin");
            var dest = Path.Combine(destDir, "big.bin");
            WriteFilled(source, 32 * 1024 * 1024, 0x55);
            var file = Record("big.bin", source, dest, new FileInfo(source).Length);
            using var cts = new CancellationTokenSource();
            var seen = new List<string>();
            var poll = Task.Run(() =>
            {
                var started = System.Diagnostics.Stopwatch.StartNew();
                while (started.Elapsed.TotalSeconds < 20 && !cts.IsCancellationRequested)
                {
                    if (!Directory.Exists(destDir))
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    foreach (var path in Directory.EnumerateFiles(destDir))
                    {
                        if (path.Contains(".mercury-speed-test.tmp.", StringComparison.Ordinal))
                        {
                            seen.Add(path);
                            cts.Cancel();
                            return;
                        }
                    }

                    Thread.Sleep(1);
                }
            });

            OperationCanceledException? cancelled = null;
            ProbeResult? result = null;
            try
            {
                result = await AdaptiveCopyProbe.MeasureAsync([file], cts.Token);
            }
            catch (OperationCanceledException ex)
            {
                cancelled = ex;
            }

            await poll;
            Assert.True(cancelled is not null || result is { Failed: false });
            Assert.False(File.Exists(dest));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(destDir),
                path => path.Contains(".mercury-speed-test.tmp.", StringComparison.Ordinal));
            if (seen.Count > 0)
            {
                Assert.All(seen, path =>
                {
                    Assert.Contains(".mercury-speed-test.tmp.", Path.GetFileName(path));
                    Assert.NotEqual(dest, path);
                });
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task TwoRangesWriteNewNamesForOnePendingLargeFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-probe-ranges-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FileStream? locked = null;
        try
        {
            var sourceDir = Path.Combine(root, "source");
            var destDir = Path.Combine(root, "dest");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(destDir);
            var copiedSource = Path.Combine(sourceDir, "already-large.bin");
            var pendingSource = Path.Combine(sourceDir, "pending-large.bin");
            var copiedDest = Path.Combine(destDir, "already-large.bin");
            var pendingDest = Path.Combine(destDir, "pending-large.bin");
            WriteFilled(copiedSource, 64 * 1024, 0x11);
            WriteFilled(pendingSource, 1024 * 1024, 0x7A);
            File.WriteAllBytes(copiedDest, "COPIED-LARGE"u8.ToArray());
            File.WriteAllBytes(pendingDest, "FINAL-LARGE"u8.ToArray());

            var job = new Job { Name = "probe-ranges", SourcePath = sourceDir, DestinationPath = destDir };
            using var journal = JobJournal.Create(Path.Combine(root, "journal"), job);
            journal.UpsertFile(Record("already-large.bin", copiedSource, copiedDest, AdaptiveCopyPolicy.LargeFileBytes + (8 * 1024 * 1024)));
            journal.MarkCopied("already-large.bin", "large-hash");
            journal.UpsertFile(Record("pending-large.bin", pendingSource, pendingDest, AdaptiveCopyPolicy.LargeFileBytes));
            var files = journal.GetFiles();
            locked = new FileStream(copiedSource, FileMode.Open, FileAccess.Read, FileShare.None);

            var seen = new List<string>();
            var result = await AdaptiveCopyProbe.MeasureAsync(files, CancellationToken.None, seen.Add);
            Assert.False(result.Failed);
            var ranges = Assert.Single(result.Samples, sample => sample.Mode == AdaptiveCopyMode.Ranges2);
            var paths = ranges.TestPaths.Split('|', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, paths.Length);
            Assert.NotEqual(paths[0], paths[1]);
            Assert.True(ranges.Bytes > 0);
            Assert.All(paths, path => Assert.Contains(path, seen));
            Assert.DoesNotContain(seen, path => path.Contains("already-large.bin" + AdaptiveCopyProbe.SpeedTestMarker, StringComparison.Ordinal));
            foreach (var path in paths)
            {
                var leaf = Path.GetFileName(path);
                Assert.StartsWith("pending-large.bin" + AdaptiveCopyProbe.SpeedTestMarker, leaf, StringComparison.Ordinal);
                Assert.Contains(".mercury-speed-test.tmp.", leaf);
                Assert.Equal(destDir, Path.GetDirectoryName(path));
                Assert.NotEqual(Path.GetFullPath(pendingDest), path);
                Assert.NotEqual(Path.GetFullPath(copiedDest), path);
                Assert.False(File.Exists(path));
            }

            Assert.Equal("FINAL-LARGE"u8.ToArray(), File.ReadAllBytes(pendingDest));
            Assert.Equal("COPIED-LARGE"u8.ToArray(), File.ReadAllBytes(copiedDest));
            Assert.DoesNotContain(Directory.EnumerateFiles(destDir), path => path.Contains(".mercury-speed-test.tmp.", StringComparison.Ordinal));
            Assert.Equal(FileCopyStatus.Pending, journal.GetFiles().Single(file => file.RelativePath == "pending-large.bin").Status);
            Assert.Equal(FileCopyStatus.Copied, journal.GetFiles().Single(file => file.RelativePath == "already-large.bin").Status);
            var line = AdaptiveCopyLog.ProbeLine(result.Samples, result.ElapsedSeconds, result.Mode);
            Assert.Contains("pending-large.bin.mercury-speed-test.tmp.", line);
            Assert.Contains("MB/s", line);
            Assert.DoesNotContain("already-large.bin.mercury-speed-test.tmp.", line);
        }
        finally
        {
            locked?.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SubSecondSampleDoesNotSwitchToTwoOrFourFiles()
    {
        const long bytes = 8_000_000;
        var brief = new[]
        {
            new ProbeSample(AdaptiveCopyMode.Sequential, bytes, 0.20),
            new ProbeSample(AdaptiveCopyMode.Files2, bytes, 0.12),
            new ProbeSample(AdaptiveCopyMode.Files4, bytes, 0.08)
        };
        var raw = new Dictionary<AdaptiveCopyMode, double>
        {
            [AdaptiveCopyMode.Sequential] = AdaptiveCopyPolicy.Rate(bytes, 0.20),
            [AdaptiveCopyMode.Files2] = AdaptiveCopyPolicy.Rate(bytes, 0.12),
            [AdaptiveCopyMode.Files4] = AdaptiveCopyPolicy.Rate(bytes, 0.08)
        };
        Assert.Equal(AdaptiveCopyMode.Files4, AdaptiveCopyPolicy.Choose(raw));
        Assert.Equal(AdaptiveCopyMode.Sequential, AdaptiveCopyPolicy.ChooseFromSamples(brief));
        var ruled = AdaptiveCopyLog.ProbeLine(brief, 0.40, AdaptiveCopyPolicy.ChooseFromSamples(brief));
        Assert.Contains("Chose one stream", ruled);
        Assert.Contains("too fast to measure this destination", ruled);
        Assert.Contains("staying on one stream", ruled);
        Assert.Contains("is not the baseline", ruled);
        Assert.DoesNotContain("Chose 2 files", ruled);
        Assert.DoesNotContain("Chose 4 files", ruled);

        var fastWide = new[]
        {
            new ProbeSample(AdaptiveCopyMode.Sequential, bytes, 2.0),
            new ProbeSample(AdaptiveCopyMode.Files4, bytes, 0.30)
        };
        Assert.Equal(AdaptiveCopyMode.Sequential, AdaptiveCopyPolicy.ChooseFromSamples(fastWide));
        Assert.Contains("too fast to measure this destination", AdaptiveCopyLog.ProbeLine(fastWide, 2.3, AdaptiveCopyMode.Sequential));

        var measured = new[]
        {
            new ProbeSample(AdaptiveCopyMode.Sequential, bytes, 2.0),
            new ProbeSample(AdaptiveCopyMode.Files4, bytes, 1.2)
        };
        Assert.Equal(AdaptiveCopyMode.Files4, AdaptiveCopyPolicy.ChooseFromSamples(measured));

        var root = Path.Combine(Path.GetTempPath(), "mercury-probe-brief-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var dest = Path.Combine(root, "dest");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(dest);
        try
        {
            var files = new List<FileRecord>();
            for (var i = 0; i < 4; i++)
            {
                var name = "f" + i + ".bin";
                var src = Path.Combine(source, name);
                var dst = Path.Combine(dest, name);
                WriteFilled(src, 320 * 1024, (byte)(0x30 + i));
                files.Add(Record(name, src, dst, new FileInfo(src).Length));
            }

            var result = await AdaptiveCopyProbe.MeasureAsync(files, CancellationToken.None);
            Assert.False(result.Failed);
            Assert.NotEmpty(result.Samples);
            Assert.All(result.Samples, sample => Assert.True(sample.Seconds < AdaptiveCopyPolicy.ProbeMinSampleSeconds, sample.Mode + " took " + sample.Seconds.ToString("0.000") + "s"));
            Assert.Equal(AdaptiveCopyMode.Sequential, result.Mode);
            Assert.Equal(AdaptiveCopyMode.Sequential, AdaptiveCopyPolicy.ChooseFromSamples(result.Samples));
            var line = AdaptiveCopyLog.ProbeLine(result.Samples, result.ElapsedSeconds, result.Mode);
            Assert.Contains("too fast to measure this destination", line);
            Assert.Contains("staying on one stream", line);
            Assert.Contains("Chose one stream", line);
            Assert.DoesNotContain("Chose 2 files", line);
            Assert.DoesNotContain("Chose 4 files", line);
            Assert.Empty(Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void AssertSamplePaths(
        ProbeResult result,
        AdaptiveCopyMode mode,
        int names,
        IReadOnlyList<FileRecord> files)
    {
        var sample = Assert.Single(result.Samples, s => s.Mode == mode);
        var paths = sample.TestPaths.Split('|', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(names, paths.Length);
        Assert.Equal(names, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(sample.Bytes > 0);
        Assert.True(sample.Seconds >= 0);
        var finals = new HashSet<string>(files.Select(f => Path.GetFullPath(f.DestPath)), StringComparer.OrdinalIgnoreCase);
        var copiedLeaf = "already-copied.bin";
        foreach (var path in paths)
        {
            var leaf = Path.GetFileName(path);
            Assert.Matches(@"^.+\.mercury-speed-test\.tmp\.[0-9a-fA-F]{32}$", leaf);
            Assert.DoesNotContain(finals, final => string.Equals(final, path, StringComparison.OrdinalIgnoreCase));
            Assert.False(leaf.StartsWith(copiedLeaf, StringComparison.OrdinalIgnoreCase));
            var folder = Path.GetDirectoryName(path);
            Assert.Contains(files.Where(f => f.Status != FileCopyStatus.Copied), file =>
                string.Equals(Path.GetDirectoryName(Path.GetFullPath(file.DestPath)), folder, StringComparison.OrdinalIgnoreCase)
                && leaf.StartsWith(Path.GetFileName(file.DestPath) + AdaptiveCopyProbe.SpeedTestMarker, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(paths, path => Path.GetFileName(path).StartsWith(copiedLeaf, StringComparison.OrdinalIgnoreCase));
    }

    private static FileRecord Record(string relative, string source, string dest, long size) => new()
    {
        RelativePath = relative,
        SourcePath = source,
        DestPath = dest,
        Size = size,
        LastWriteUtc = DateTime.UtcNow,
        Status = FileCopyStatus.Pending
    };

    private static void WriteFilled(string path, int size, byte fill)
    {
        var data = new byte[size];
        Array.Fill(data, fill);
        File.WriteAllBytes(path, data);
    }
}
