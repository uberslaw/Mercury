using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mercury;

internal static class FileCopier
{
    private const FileOptions NoBufferingFlag = (FileOptions)0x20000000;
    private static readonly AsyncLocal<string?> ProgressPath = new();

    private static void NoteBytes(PauseGate? pause, long count)
    {
        var path = ProgressPath.Value;
        if (string.IsNullOrEmpty(path))
        {
            pause?.AddFileBytes(count);
        }
        else
        {
            pause?.AddFileBytes(count, path);
        }
    }

    public static async Task<string?> CopyAsync(
        Job job,
        FileRecord file,
        BandwidthBudget budget,
        PauseGate? pause,
        SpeedTracker? speed,
        CancellationToken cancellationToken,
        IJobLog? log,
        string? jobName,
        UnbufferedIoSession? io,
        Action<long>? onCopied = null)
    {
        var done = await CopyAdaptiveAsync(
                job, file, budget, pause, speed, cancellationToken, log, jobName, io, onCopied)
            .ConfigureAwait(false);
        return done.Hash;
    }

    internal static async Task<AdaptiveFileCopy> CopyAdaptiveAsync(
        Job job,
        FileRecord file,
        BandwidthBudget budget,
        PauseGate? pause,
        SpeedTracker? speed,
        CancellationToken cancellationToken,
        IJobLog? log,
        string? jobName,
        UnbufferedIoSession? io,
        Action<long>? onCopied = null,
        bool stripe = false,
        long? maxNewBytes = null)
    {
        var name = jobName ?? (string.IsNullOrWhiteSpace(job.Name) ? job.Id : job.Name);
        ProgressPath.Value = file.RelativePath;
        try
        {
        if (job.Options.CopySymbolicLinksAsLinks &&
            FileMetadata.TryCopySymlinkFile(file.SourcePath, file.DestPath, log, job.Id, name))
        {
            FileMetadata.ApplyCopiedFile(file.SourcePath, file.DestPath, job.Options, log, job.Id, name);
            return new AdaptiveFileCopy(true, null, file.Size, false);
        }

        if (stripe && file.Size >= 2)
        {
            var striped = await CopyStripedAsync(
                    job, file, budget, pause, speed, cancellationToken, log, name, onCopied, maxNewBytes)
                .ConfigureAwait(false);
            if (!striped.Fallback)
            {
                return striped;
            }

            log?.Info(job.Id, name, $"Split copy of {file.RelativePath} fell back to one stream.");
        }

        var destDir = Path.GetDirectoryName(file.DestPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        var temp = file.DestPath + ".mercury.tmp";
        var resumeAt = ResumeOffset(file.SourcePath, temp, file.Size);
        if (resumeAt > 0)
        {
            log?.Info(job.Id, name,
                $"Resuming {file.RelativePath} at {ByteFormatter.ToString(resumeAt)} of {ByteFormatter.ToString(file.Size)}.");
            NoteBytes(pause, resumeAt);
            onCopied?.Invoke(resumeAt);
        }

        XxHash64? hasher = job.Options.Verify == VerifyLevel.Thorough ? new XxHash64() : null;
        var sector = VolumeInfo.GetBytesPerSector(file.DestPath);
        io ??= new UnbufferedIoSession(job.Options, new PayloadInventory());
        var copied = resumeAt;

        try
        {
            if (hasher is not null && resumeAt > 0 && resumeAt <= 32L * 1024 * 1024)
            {
                HashPrefix(temp, resumeAt, hasher);
            }
            else if (hasher is not null && resumeAt > 32L * 1024 * 1024)
            {
                hasher = null;
            }

            var allowance = maxNewBytes ?? long.MaxValue;
            if (maxNewBytes is null &&
                copied == 0 &&
                (io.NeedsBufferedSample(file.SourcePath, file.Size) ||
                 io.NeedsUnbufferedSample(file.SourcePath, file.Size)))
            {
                copied = await ProbeCopyAsync(
                        job, file, temp, budget, pause, speed, hasher, io, sector, log, name, cancellationToken)
                    .ConfigureAwait(false);
                onCopied?.Invoke(copied);
            }

            if (copied < file.Size && allowance > 0)
            {
                var start = copied;
                var count = (long)Math.Min(file.Size - copied, allowance);
                var unbuffered = copied == 0 && maxNewBytes is null && io.UseUnbufferedFor(file.SourcePath, file.Size);
                copied += await CopyRangeAsync(
                        file.SourcePath,
                        temp,
                        copied,
                        count,
                        unbuffered,
                        sector,
                        job,
                        budget,
                        pause,
                        speed,
                        hasher,
                        createDest: copied == 0,
                        cancellationToken,
                        onCopied: n => onCopied?.Invoke(start + n))
                    .ConfigureAwait(false);
            }

            if (copied < file.Size && maxNewBytes is not null)
            {
                onCopied?.Invoke(copied);
                return new AdaptiveFileCopy(false, null, copied, false);
            }

            Truncate(temp, copied);
            if (File.Exists(file.DestPath))
            {
                File.Delete(file.DestPath);
            }

            File.Move(temp, file.DestPath);
            FileMetadata.ApplyCopiedFile(file.SourcePath, file.DestPath, job.Options, log, job.Id, name);
            onCopied?.Invoke(copied);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        TryDelete(PartPath(file.DestPath, 0));
        TryDelete(PartPath(file.DestPath, 1));
        var hash = hasher is null ? null : HashUtil.ToHex(hasher.GetCurrentHash());
        return new AdaptiveFileCopy(true, hash, copied, false);
        }
        finally
        {
            ProgressPath.Value = null;
        }
    }

    private const int PrefixCheckBytes = 64 * 1024;

    internal static long ResumeOffset(string source, string temp, long size)
    {
        if (size <= 0 || !File.Exists(temp))
        {
            return 0;
        }

        long length;
        try
        {
            length = new FileInfo(temp).Length;
        }
        catch
        {
            return 0;
        }

        if (length <= 0 || length > size)
        {
            return 0;
        }

        if (length == size)
        {
            return size;
        }

        return PrefixTailMatches(source, temp, length) ? length : 0;
    }

    private static bool PrefixTailMatches(string source, string temp, long length)
    {
        var check = (int)Math.Min(PrefixCheckBytes, length);
        var offset = length - check;
        try
        {
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, check);
            using var dst = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, check);
            if (src.Length < length)
            {
                return false;
            }

            src.Seek(offset, SeekOrigin.Begin);
            dst.Seek(offset, SeekOrigin.Begin);
            var a = new byte[check];
            var b = new byte[check];
            return src.Read(a) == check && dst.Read(b) == check && a.AsSpan().SequenceEqual(b);
        }
        catch
        {
            return false;
        }
    }

    private static void HashPrefix(string path, long count, XxHash64 hasher)
    {
        if (count <= 0)
        {
            return;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, HashUtil.BufferSize);
        var buffer = new byte[HashUtil.BufferSize];
        var remaining = count;
        while (remaining > 0)
        {
            var take = (int)Math.Min(buffer.Length, remaining);
            var read = stream.Read(buffer, 0, take);
            if (read <= 0)
            {
                break;
            }

            hasher.Append(buffer.AsSpan(0, read));
            remaining -= read;
        }
    }

    private static async Task<long> ProbeCopyAsync(
        Job job,
        FileRecord file,
        string temp,
        BandwidthBudget budget,
        PauseGate? pause,
        SpeedTracker? speed,
        XxHash64? hasher,
        UnbufferedIoSession io,
        int sector,
        IJobLog? log,
        string name,
        CancellationToken cancellationToken)
    {
        var offset = 0L;
        var sample = io.SampleBytes(file.Size, sector);
        if (sample <= 0)
        {
            io.Skip("file too small to probe");
            return 0;
        }

        if (io.NeedsBufferedSample(file.SourcePath, file.Size))
        {
            if (budget.IsIdleThrottleCapping())
            {
                io.Skip("idle throttle is capping");
                return 0;
            }

            var buffered = await CopyRangeMeasuredAsync(
                    file.SourcePath, temp, 0, Math.Min(sample, file.Size), unbuffered: false, sector,
                    job, budget, pause, speed, hasher, createDest: true, cancellationToken)
                .ConfigureAwait(false);
            offset = buffered.copied;
            if (budget.IsNearCap(buffered.bytesPerSecond, job.Options.MaxBytesPerSecond, io.Policy.ThrottleSlack) ||
                budget.IsIdleThrottleCapping())
            {
                io.Skip("already at throttle");
                log?.Info(job.Id, name, "Unbuffered probe skipped — already at throttle (unbuffered cannot beat a cap).");
                return offset;
            }

            io.NoteBuffered(buffered.bytesPerSecond);
            if (offset >= file.Size)
            {
                return offset;
            }
        }

        if (!io.NeedsUnbufferedSample(file.SourcePath, file.Size))
        {
            return offset;
        }

        var second = Math.Min(sample, file.Size - offset);
        second = AlignDown(second, sector);
        if (second <= 0)
        {
            return offset;
        }

        try
        {
            var unbuf = await CopyRangeMeasuredAsync(
                    file.SourcePath, temp, offset, second, unbuffered: true, sector,
                    job, budget, pause, speed, hasher, createDest: offset == 0, cancellationToken)
                .ConfigureAwait(false);
            offset += unbuf.copied;
            if (io.BufferedBytesPerSecond is { } bufferedBps)
            {
                var line = io.Complete(bufferedBps, unbuf.bytesPerSecond);
                log?.Info(job.Id, name, line);
            }
            else
            {
                io.NoteBuffered(0);
                io.Complete(0, unbuf.bytesPerSecond);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            io.UnbufferedUnavailable();
            log?.Info(job.Id, name, $"Unbuffered I/O failed ({ex.Message}); staying buffered.");
        }

        return offset;
    }

    private static async Task<(long copied, double bytesPerSecond)> CopyRangeMeasuredAsync(
        string source,
        string dest,
        long offset,
        long count,
        bool unbuffered,
        int sector,
        Job job,
        BandwidthBudget budget,
        PauseGate? pause,
        SpeedTracker? speed,
        XxHash64? hasher,
        bool createDest,
        CancellationToken cancellationToken)
    {
        var paused = 0L;
        var clock = Stopwatch.StartNew();
        var copied = await CopyRangeAsync(
                source, dest, offset, count, unbuffered, sector, job, budget, pause, speed, hasher, createDest,
                cancellationToken,
                onPauseWait: ticks => Interlocked.Add(ref paused, ticks))
            .ConfigureAwait(false);
        clock.Stop();
        var elapsed = clock.Elapsed.TotalSeconds - paused / (double)Stopwatch.Frequency;
        if (elapsed < 0.001)
        {
            elapsed = 0.001;
        }

        return (copied, copied / elapsed);
    }

    private static async Task<long> CopyRangeAsync(
        string source,
        string dest,
        long offset,
        long count,
        bool unbuffered,
        int sector,
        Job job,
        BandwidthBudget budget,
        PauseGate? pause,
        SpeedTracker? speed,
        XxHash64? hasher,
        bool createDest,
        CancellationToken cancellationToken,
        Action<long>? onPauseWait = null,
        Action<long>? onCopied = null)
    {
        if (count <= 0)
        {
            return 0;
        }

        if (unbuffered)
        {
            try
            {
                return await CopyRangeUnbufferedAsync(
                        source, dest, offset, count, sector, job, budget, pause, speed, hasher, createDest,
                        cancellationToken, onPauseWait, onCopied)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await CopyRangeStreamAsync(
                        source, dest, offset, count, writeThrough: true, job, budget, pause, speed, hasher,
                        createDest, cancellationToken, onPauseWait, onCopied)
                    .ConfigureAwait(false);
            }
        }

        return await CopyRangeStreamAsync(
                source, dest, offset, count, writeThrough: false, job, budget, pause, speed, hasher,
                createDest, cancellationToken, onPauseWait, onCopied)
            .ConfigureAwait(false);
    }

    private static async Task<long> CopyRangeStreamAsync(
        string source,
        string dest,
        long offset,
        long count,
        bool writeThrough,
        Job job,
        BandwidthBudget budget,
        PauseGate? pause,
        SpeedTracker? speed,
        XxHash64? hasher,
        bool createDest,
        CancellationToken cancellationToken,
        Action<long>? onPauseWait,
        Action<long>? onCopied = null)
    {
        var flags = FileOptions.SequentialScan | FileOptions.Asynchronous;
        if (writeThrough)
        {
            flags |= FileOptions.WriteThrough;
        }

        var buffer = new byte[HashUtil.BufferSize];
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, HashUtil.BufferSize, flags);
        await using var dst = new FileStream(
            dest,
            createDest && offset == 0 ? FileMode.Create : FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.None,
            HashUtil.BufferSize,
            flags);
        src.Seek(offset, SeekOrigin.Begin);
        dst.Seek(offset, SeekOrigin.Begin);
        var remaining = count;
        var copied = 0L;
        var lastReport = 0L;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitPauseAsync(pause, cancellationToken, onPauseWait).ConfigureAwait(false);
            var take = (int)Math.Min(buffer.Length, remaining);
            var read = await src.ReadAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await budget.ConsumeAsync(job.Id, job.Options.MaxBytesPerSecond, read, cancellationToken)
                .ConfigureAwait(false);
            await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hasher?.Append(buffer.AsSpan(0, read));
            speed?.Add(read);
            NoteBytes(pause, read);
            remaining -= read;
            copied += read;
            if (copied - lastReport >= 4L * 1024 * 1024)
            {
                onCopied?.Invoke(copied);
                lastReport = copied;
            }
        }

        await dst.FlushAsync(cancellationToken).ConfigureAwait(false);
        onCopied?.Invoke(copied);
        return copied;
    }

    private static async Task<long> CopyRangeUnbufferedAsync(
        string source,
        string dest,
        long offset,
        long count,
        int sector,
        Job job,
        BandwidthBudget budget,
        PauseGate? pause,
        SpeedTracker? speed,
        XxHash64? hasher,
        bool createDest,
        CancellationToken cancellationToken,
        Action<long>? onPauseWait,
        Action<long>? onCopied = null)
    {
        sector = Math.Max(512, sector);
        var flags = FileOptions.SequentialScan | FileOptions.Asynchronous | FileOptions.WriteThrough | NoBufferingFlag;
        using var src = OpenHandle(
            source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, flags);
        using var dst = OpenHandle(
            dest,
            createDest && offset == 0 ? FileMode.Create : FileMode.Open,
            FileAccess.Write,
            FileShare.None,
            flags);
        using var aligned = new AlignedBuffer(HashUtil.BufferSize, sector);
        var remaining = count;
        var copied = 0L;
        var pos = offset;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitPauseAsync(pause, cancellationToken, onPauseWait).ConfigureAwait(false);
            var useful = (int)Math.Min(aligned.Length, remaining);
            var request = AlignUp(useful, sector);
            var memory = aligned.Memory[..request];
            memory.Span.Clear();
            var read = await RandomAccess.ReadAsync(src, memory, pos, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            var payload = (int)Math.Min(read, remaining);
            hasher?.Append(memory.Span[..payload]);
            var writeLen = AlignUp(payload, sector);
            await budget.ConsumeAsync(job.Id, job.Options.MaxBytesPerSecond, payload, cancellationToken)
                .ConfigureAwait(false);
            await RandomAccess.WriteAsync(dst, memory[..writeLen], pos, cancellationToken).ConfigureAwait(false);
            speed?.Add(payload);
            NoteBytes(pause, payload);
            remaining -= payload;
            copied += payload;
            pos += payload;
            onCopied?.Invoke(copied);
        }

        return copied;
    }

    private static SafeFileHandle OpenHandle(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        FileOptions options)
    {
        try
        {
            return File.OpenHandle(path, mode, access, share, options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            var fallback = options & ~NoBufferingFlag;
            return File.OpenHandle(path, mode, access, share, fallback);
        }
    }

    private static async Task WaitPauseAsync(PauseGate? pause, CancellationToken cancellationToken, Action<long>? onPauseWait)
    {
        if (pause is null)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
        onPauseWait?.Invoke(Stopwatch.GetTimestamp() - started);
    }

    private static void Truncate(string path, long length)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            if (stream.Length != length)
            {
                stream.SetLength(length);
            }
        }
        catch
        {
            // dest size is still checked at verify
        }
    }

    private static long AlignDown(long value, int sector)
    {
        if (sector <= 0)
        {
            return value;
        }

        return value / sector * sector;
    }

    private static int AlignUp(int value, int sector)
    {
        if (sector <= 0)
        {
            return value;
        }

        return (value + sector - 1) / sector * sector;
    }

    private static string PartPath(string dest, int part) => dest + ".mercury.r" + part + ".tmp";

    private static async Task<AdaptiveFileCopy> CopyStripedAsync(
        Job job,
        FileRecord file,
        BandwidthBudget budget,
        PauseGate? pause,
        SpeedTracker? speed,
        CancellationToken cancellationToken,
        IJobLog? log,
        string name,
        Action<long>? onCopied,
        long? maxNewBytes)
    {
        if (file.Size < 2)
        {
            return new AdaptiveFileCopy(false, null, 0, true);
        }

        var legacy = file.DestPath + ".mercury.tmp";
        var part0 = PartPath(file.DestPath, 0);
        var part1 = PartPath(file.DestPath, 1);
        if (File.Exists(legacy) && SafeLength(legacy) > 0 && !File.Exists(part0) && !File.Exists(part1))
        {
            return new AdaptiveFileCopy(false, null, 0, true);
        }

        var destDir = Path.GetDirectoryName(file.DestPath);
        if (!string.IsNullOrEmpty(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        var mid = file.Size / 2;
        var ranges = new[] { (Offset: 0L, Length: mid), (Offset: mid, Length: file.Size - mid) };
        var parts = new[] { part0, part1 };
        try
        {
            if (maxNewBytes is null)
            {
                await Task.WhenAll(
                    CopyPartAsync(file, ranges[0].Offset, ranges[0].Length, parts[0], null, job, budget, pause, speed, cancellationToken, onCopied, parts),
                    CopyPartAsync(file, ranges[1].Offset, ranges[1].Length, parts[1], null, job, budget, pause, speed, cancellationToken, onCopied, parts))
                    .ConfigureAwait(false);
            }
            else
            {
                var budgetLeft = maxNewBytes.Value;
                for (var i = 0; i < parts.Length; i++)
                {
                    var wrote = await CopyPartAsync(
                            file, ranges[i].Offset, ranges[i].Length, parts[i], budgetLeft, job, budget, pause, speed,
                            cancellationToken, onCopied, parts)
                        .ConfigureAwait(false);
                    budgetLeft -= wrote;
                    if (SafeLength(parts[i]) < ranges[i].Length)
                    {
                        return new AdaptiveFileCopy(false, null, SumLengths(parts), false);
                    }
                }
            }

            if (SafeLength(part0) < ranges[0].Length || SafeLength(part1) < ranges[1].Length)
            {
                return new AdaptiveFileCopy(false, null, SumLengths(parts), false);
            }

            var hash = await AssembleStripedAsync(job, file, parts, log, name, cancellationToken).ConfigureAwait(false);
            TryDelete(part0);
            TryDelete(part1);
            TryDelete(legacy);
            onCopied?.Invoke(file.Size);
            return new AdaptiveFileCopy(true, hash, file.Size, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Info(job.Id, name, $"Split copy of {file.RelativePath} fell back to one stream: {ex.Message}");
            return new AdaptiveFileCopy(false, null, SumLengths(parts), true);
        }
    }

    private static async Task<long> CopyPartAsync(
        FileRecord file,
        long sourceOffset,
        long length,
        string partPath,
        long? maxNewBytes,
        Job job,
        BandwidthBudget budget,
        PauseGate? pause,
        SpeedTracker? speed,
        CancellationToken cancellationToken,
        Action<long>? onCopied,
        string[] allParts)
    {
        var have = ExistingPartLength(file.SourcePath, partPath, sourceOffset, length);
        if (have >= length || maxNewBytes is 0)
        {
            return 0;
        }

        var room = maxNewBytes ?? long.MaxValue;
        var take = Math.Min(length - have, room);
        if (take <= 0)
        {
            return 0;
        }

        var wrote = await CopySliceAsync(
                file.SourcePath,
                sourceOffset + have,
                partPath,
                have,
                take,
                job,
                budget,
                pause,
                speed,
                cancellationToken,
                () => onCopied?.Invoke(SumLengths(allParts)))
            .ConfigureAwait(false);
        return wrote;
    }

    private static async Task<long> CopySliceAsync(
        string source,
        long sourceOffset,
        string dest,
        long destOffset,
        long count,
        Job job,
        BandwidthBudget budget,
        PauseGate? pause,
        SpeedTracker? speed,
        CancellationToken cancellationToken,
        Action? onCopied)
    {
        var flags = FileOptions.SequentialScan | FileOptions.Asynchronous;
        var buffer = new byte[HashUtil.BufferSize];
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, HashUtil.BufferSize, flags);
        await using var dst = new FileStream(
            dest,
            destOffset == 0 ? FileMode.Create : FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.None,
            HashUtil.BufferSize,
            flags);
        src.Seek(sourceOffset, SeekOrigin.Begin);
        dst.Seek(destOffset, SeekOrigin.Begin);
        var remaining = count;
        var copied = 0L;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pause is not null)
            {
                await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            }

            var take = (int)Math.Min(buffer.Length, remaining);
            var read = await src.ReadAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await budget.ConsumeAsync(job.Id, job.Options.MaxBytesPerSecond, read, cancellationToken).ConfigureAwait(false);
            await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            speed?.Add(read);
            NoteBytes(pause, read);
            remaining -= read;
            copied += read;
            onCopied?.Invoke();
        }

        await dst.FlushAsync(cancellationToken).ConfigureAwait(false);
        return copied;
    }

    private static long ExistingPartLength(string source, string part, long sourceOffset, long expected)
    {
        var length = SafeLength(part);
        if (length <= 0)
        {
            return 0;
        }

        if (length == expected)
        {
            return expected;
        }

        if (length > expected)
        {
            TryDelete(part);
            return 0;
        }

        if (!SliceTailMatches(source, part, sourceOffset, length))
        {
            TryDelete(part);
            return 0;
        }

        return length;
    }

    private static bool SliceTailMatches(string source, string part, long sourceOffset, long length)
    {
        var check = (int)Math.Min(PrefixCheckBytes, length);
        var destOffset = length - check;
        var srcOffset = sourceOffset + destOffset;
        try
        {
            using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, check);
            using var dst = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, check);
            src.Seek(srcOffset, SeekOrigin.Begin);
            dst.Seek(destOffset, SeekOrigin.Begin);
            var a = new byte[check];
            var b = new byte[check];
            return src.Read(a) == check && dst.Read(b) == check && a.AsSpan().SequenceEqual(b);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> AssembleStripedAsync(
        Job job,
        FileRecord file,
        string[] parts,
        IJobLog? log,
        string name,
        CancellationToken cancellationToken)
    {
        var temp = file.DestPath + ".mercury.tmp";
        XxHash64? hasher = job.Options.Verify == VerifyLevel.Thorough ? new XxHash64() : null;
        var buffer = new byte[HashUtil.BufferSize];
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, HashUtil.BufferSize, FileOptions.Asynchronous))
        {
            foreach (var part in parts)
            {
                await using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, HashUtil.BufferSize, FileOptions.Asynchronous);
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hasher?.Append(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(file.DestPath))
        {
            File.Delete(file.DestPath);
        }

        File.Move(temp, file.DestPath);
        FileMetadata.ApplyCopiedFile(file.SourcePath, file.DestPath, job.Options, log, job.Id, name);
        return hasher is null ? null : HashUtil.ToHex(hasher.GetCurrentHash());
    }

    private static long SumLengths(string[] parts)
    {
        long sum = 0;
        foreach (var part in parts)
        {
            sum += SafeLength(part);
        }

        return sum;
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}

internal sealed unsafe class AlignedBuffer : MemoryManager<byte>
{
    private void* _ptr;
    private readonly int _length;

    public AlignedBuffer(int length, int alignment)
    {
        alignment = alignment <= 0 ? 4096 : alignment;
        if ((alignment & (alignment - 1)) != 0)
        {
            alignment = 4096;
        }

        length = (length + alignment - 1) / alignment * alignment;
        _ptr = NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment);
        if (_ptr is null)
        {
            throw new OutOfMemoryException("Could not allocate an aligned copy buffer.");
        }

        _length = length;
    }

    public int Length => _length;

    public override Span<byte> GetSpan() => new(_ptr, _length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex >= (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        return new MemoryHandle((byte*)_ptr + elementIndex);
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (_ptr is not null)
        {
            NativeMemory.AlignedFree(_ptr);
            _ptr = null;
        }
    }
}
