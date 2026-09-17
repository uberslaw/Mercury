using System.Diagnostics;

namespace Mercury.Tests;

public class BandwidthBudgetTests
{
    [Fact]
    public async Task JobMaxCapsASingleStream()
    {
        var budget = new BandwidthBudget();
        const int chunk = 64 * 1024;
        const double jobMax = 2 * 1024 * 1024;
        var total = 0;
        var sw = Stopwatch.StartNew();
        while (total < 8 * 1024 * 1024)
        {
            await budget.ConsumeAsync("job-a", jobMax, chunk, CancellationToken.None);
            total += chunk;
        }

        sw.Stop();
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(900), $"Expected throttle, elapsed {sw.Elapsed}");
    }

    [Fact]
    public async Task GlobalMaxIsSharedAcrossJobs()
    {
        var budget = new BandwidthBudget
        {
            GlobalMaxBytesPerSecond = 2 * 1024 * 1024
        };

        async Task<long> Run(string id)
        {
            var n = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(1.2))
            {
                await budget.ConsumeAsync(id, jobMaxBytesPerSecond: 10 * 1024 * 1024, 32 * 1024, CancellationToken.None);
                n += 32 * 1024;
            }

            return n;
        }

        var a = Run("a");
        var b = Run("b");
        var bytes = await a + await b;
        // Two jobs with high per-job caps should still sit near the 2 MB/s global max.
        Assert.True(bytes < 6 * 1024 * 1024, $"Combined {bytes} bytes exceeded a reasonable global cap window");
        Assert.True(bytes > 512 * 1024, "Should have transferred something");
    }

    [Fact]
    public async Task IdleThrottleCapsWhenMachineIsBusy()
    {
        var budget = new BandwidthBudget
        {
            IdleThrottleBytesPerSecond = 2 * 1024 * 1024,
            MachineLoad = new FixedMachineLoad { IsBusy = true }
        };

        const int chunk = 64 * 1024;
        var total = 0;
        var sw = Stopwatch.StartNew();
        while (total < 8 * 1024 * 1024)
        {
            await budget.ConsumeAsync("job-a", jobMaxBytesPerSecond: null, chunk, CancellationToken.None);
            total += chunk;
        }

        sw.Stop();
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(900), $"Expected idle throttle, elapsed {sw.Elapsed}");
    }

    [Fact]
    public async Task IdleThrottleDoesNotCapWhenIdle()
    {
        var budget = new BandwidthBudget
        {
            IdleThrottleBytesPerSecond = 1,
            MachineLoad = new FixedMachineLoad { IsBusy = false }
        };

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            await budget.ConsumeAsync("job-a", jobMaxBytesPerSecond: null, 64 * 1024, CancellationToken.None);
        }

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(800), $"Idle should not throttle, elapsed {sw.Elapsed}");
    }

    [Fact]
    public void OtherCpuPercentExcludesThisProcess()
    {
        // 50% total busy, 40% this process → 10% others (below 18% idle threshold).
        var other = MachineLoadSampler.OtherCpuPercent(
            idle1: 50, kernel1: 100, user1: 0, proc1: TimeSpan.Zero,
            idle2: 100, kernel2: 200, user2: 0, proc2: TimeSpan.FromMilliseconds(400),
            elapsedSeconds: 1,
            processorCount: 1);
        Assert.InRange(other, 9, 11);
    }

    [Fact]
    public void MinSkipsGlobalWaitWhenBelowFloor()
    {
        var budget = new BandwidthBudget
        {
            GlobalMinBytesPerSecond = 50 * 1024 * 1024,
            GlobalMaxBytesPerSecond = 1
        };

        _ = budget.TryConsume("job", jobMaxBytesPerSecond: null, bytes: 1);
        Thread.Sleep(40);
        var wait = budget.TryConsume("job", jobMaxBytesPerSecond: null, bytes: 4096);
        Assert.Equal(TimeSpan.Zero, wait);
    }
}
