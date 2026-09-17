using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mercury;

public interface IMachineLoad
{
    bool IsBusy { get; }
    void Refresh();
}

/// <summary>
/// Samples whether the PC is in use. Prefers other-process CPU (total minus this process)
/// so Mercury’s own copy does not count as “busy”. Falls back to total CPU if needed.
/// </summary>
public sealed class MachineLoadSampler : IMachineLoad, IDisposable
{
    public const double DefaultBusyPercent = 18;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(400);

    private readonly Process _self = Process.GetCurrentProcess();
    private readonly object _lock = new();
    private ulong _idle;
    private ulong _kernel;
    private ulong _user;
    private TimeSpan _proc;
    private long _stamp;
    private bool _hasSample;
    private bool _busy;

    public double BusyThresholdPercent { get; set; } = DefaultBusyPercent;

    public bool IsBusy
    {
        get
        {
            lock (_lock)
            {
                return _busy;
            }
        }
    }

    public void Refresh()
    {
        var now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (_stamp != 0 && ToSeconds(now - _stamp) < SampleInterval.TotalSeconds)
            {
                return;
            }

            if (!TryReadTimes(out var idle, out var kernel, out var user, out var proc))
            {
                return;
            }

            if (_hasSample)
            {
                var elapsed = ToSeconds(now - _stamp);
                var other = OtherCpuPercent(_idle, _kernel, _user, _proc, idle, kernel, user, proc, elapsed);
                var threshold = BusyThresholdPercent > 0 ? BusyThresholdPercent : DefaultBusyPercent;
                _busy = other >= threshold;
            }

            _idle = idle;
            _kernel = kernel;
            _user = user;
            _proc = proc;
            _stamp = now;
            _hasSample = true;
        }
    }

    /// <summary>
    /// CPU % from other processes, 0–100. Negative inputs are treated as 0.
    /// </summary>
    public static double OtherCpuPercent(
        ulong idle1, ulong kernel1, ulong user1, TimeSpan proc1,
        ulong idle2, ulong kernel2, ulong user2, TimeSpan proc2,
        double elapsedSeconds,
        int processorCount = 0)
    {
        var cpus = processorCount > 0 ? processorCount : Math.Max(1, Environment.ProcessorCount);
        var idleDelta = Delta(idle1, idle2);
        var kernelDelta = Delta(kernel1, kernel2);
        var userDelta = Delta(user1, user2);
        var total = kernelDelta + userDelta;
        double totalPct = 0;
        if (total > 0)
        {
            var busy = total > idleDelta ? total - idleDelta : 0;
            totalPct = 100.0 * busy / total;
        }

        double mercuryPct = 0;
        if (elapsedSeconds > 0)
        {
            var procMs = (proc2 - proc1).TotalMilliseconds;
            if (procMs < 0)
            {
                procMs = 0;
            }

            mercuryPct = 100.0 * procMs / (elapsedSeconds * 1000.0 * cpus);
        }

        var other = totalPct - mercuryPct;
        if (other < 0)
        {
            other = 0;
        }

        if (other > 100)
        {
            other = 100;
        }

        return other;
    }

    public void Dispose() => _self.Dispose();

    private bool TryReadTimes(out ulong idle, out ulong kernel, out ulong user, out TimeSpan proc)
    {
        idle = kernel = user = 0;
        proc = TimeSpan.Zero;
        try
        {
            if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
            {
                return false;
            }

            idle = ToUInt64(idleFt);
            kernel = ToUInt64(kernelFt);
            user = ToUInt64(userFt);
            _self.Refresh();
            proc = _self.TotalProcessorTime;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ulong Delta(ulong a, ulong b) => b >= a ? b - a : 0;

    private static double ToSeconds(long ticks) =>
        (double)ticks / Stopwatch.Frequency;

    private static ulong ToUInt64(FileTime ft) => ((ulong)ft.High << 32) | ft.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }
}

public sealed class FixedMachineLoad : IMachineLoad
{
    public bool IsBusy { get; set; }

    public void Refresh()
    {
    }
}
