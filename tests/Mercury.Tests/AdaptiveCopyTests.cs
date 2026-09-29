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
}
