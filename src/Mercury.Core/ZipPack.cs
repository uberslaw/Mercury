using System.IO.Compression;
using System.IO.Hashing;

namespace Mercury;

public static class ZipPack
{
    public const string TempSuffix = ".mercury.tmp";

    public static bool Applies(Job job, CopyMapping mapping) =>
        job.Options.PackAsZip && !mapping.SingleFile;

    public static string ZipPath(CopyMapping mapping)
    {
        if (mapping.Kind == SourceKind.DriveRoot)
        {
            var root = Path.GetPathRoot(mapping.SourceRoot) ?? "drive";
            var letter = root.TrimEnd('\\', '/', ':');
            if (string.IsNullOrWhiteSpace(letter))
            {
                letter = "drive";
            }

            return Path.Combine(mapping.DestRoot, letter + "-drive.zip");
        }

        return mapping.DestRoot.TrimEnd('\\', '/') + ".zip";
    }

    public static string TempPath(string zipPath) => zipPath + TempSuffix;

    public static string EntryName(string relativePath) =>
        relativePath.Replace('\\', '/').Trim('/');

    public static IEnumerable<string> ExcludePaths(CopyMapping mapping)
    {
        var zip = ZipPath(mapping);
        yield return zip;
        yield return TempPath(zip);
    }

    public static TreeCounts CountPacked(string zipPath)
    {
        if (!File.Exists(zipPath))
        {
            return default;
        }

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var files = 0;
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (IsDirectoryEntry(entry))
                {
                    continue;
                }

                files++;
                AddFolderPrefixes(folders, entry.FullName);
            }

            return new TreeCounts(files, folders.Count);
        }
        catch
        {
            return default;
        }
    }

    public static int FolderCountFromFiles(IEnumerable<FileRecord> files)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            AddFolderPrefixes(folders, EntryName(file.RelativePath));
        }

        return folders.Count;
    }

    public static async Task PackAsync(
        string zipPath,
        IReadOnlyList<FileRecord> files,
        Job job,
        BandwidthBudget budget,
        PauseGate pause,
        SpeedTracker speed,
        IJobLog log,
        string name,
        IProgress<JobProgress>? progress,
        bool cloud,
        Func<Job, PauseGate, IJobLog, string, IProgress<JobProgress>?, bool, CancellationToken, Task> waitHours,
        Dictionary<string, string?> hashes,
        CancellationToken cancellationToken,
        Func<FileRecord, Exception, bool>? onTransientSkip = null,
        Func<string, Task>? onAfterFile = null,
        Func<ZipArchive, byte[], Task>? onRetryDeferred = null)
    {
        var parent = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var temp = TempPath(zipPath);
        TryDelete(temp);

        try
        {
            await using (var zipStream = new FileStream(
                             temp,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             HashUtil.BufferSize,
                             FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var buffer = new byte[HashUtil.BufferSize];
                var total = files.Count;
                var packed = 0;
                for (var i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    cancellationToken.ThrowIfCancellationRequested();
                    await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
                    await waitHours(job, pause, log, name, progress, cloud, cancellationToken).ConfigureAwait(false);
                    Report(progress, job, name, cloud, $"Packing {file.RelativePath}", file.RelativePath, speed);
                    pause.BeginFile(file.RelativePath, file.Size);
                    var packedOk = false;
                    try
                    {
                        await PackOneEntryAsync(
                            zip, file, job, budget, pause, speed, buffer, hashes, cancellationToken)
                            .ConfigureAwait(false);
                        packedOk = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex) when (onTransientSkip?.Invoke(file, ex) == true)
                    {
                        packedOk = false;
                    }
                    finally
                    {
                        if (!packedOk)
                        {
                            pause.EndFile();
                        }
                    }

                    if (!packedOk)
                    {
                        continue;
                    }

                    packed++;
                    if (packed == 1 || packed == total || packed % 500 == 0)
                    {
                        log.Info(job.Id, name, $"Packed {packed}/{total}: {file.RelativePath}");
                    }

                    if (onAfterFile is not null)
                    {
                        await onAfterFile(file.RelativePath).ConfigureAwait(false);
                    }

                    pause.EndFile();

                    if (onRetryDeferred is not null)
                    {
                        await onRetryDeferred(zip, buffer).ConfigureAwait(false);
                    }
                }
            }

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            File.Move(temp, zipPath);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static async Task PackOneEntryAsync(
        ZipArchive zip,
        FileRecord file,
        Job job,
        BandwidthBudget budget,
        PauseGate pause,
        SpeedTracker speed,
        byte[] buffer,
        Dictionary<string, string?> hashes,
        CancellationToken cancellationToken)
    {
        await using var src = new FileStream(
            file.SourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            HashUtil.BufferSize,
            FileMetadata.SequentialIo(job.Options));

        var entry = zip.CreateEntry(EntryName(file.RelativePath), CompressionLevel.NoCompression);
        try
        {
            entry.LastWriteTime = new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteUtc, DateTimeKind.Utc));
        }
        catch
        {
            // Zip last-write has a narrower range than NTFS.
        }

        XxHash64? hasher = job.Options.Verify == VerifyLevel.Thorough ? new XxHash64() : null;
        await using (var dst = entry.Open())
        {
            int read;
            while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
                await budget.ConsumeAsync(job.Id, job.Options.MaxBytesPerSecond, read, cancellationToken)
                    .ConfigureAwait(false);
                await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hasher?.Append(buffer.AsSpan(0, read));
                speed.Add(read);
                pause.AddFileBytes(read);
            }
        }

        hashes[file.RelativePath] = hasher is null ? null : HashUtil.ToHex(hasher.GetCurrentHash());
    }

    public static async Task AppendEntriesAsync(
        string zipPath,
        IReadOnlyList<FileRecord> files,
        Job job,
        BandwidthBudget budget,
        PauseGate pause,
        SpeedTracker speed,
        IJobLog log,
        string name,
        Dictionary<string, string?> hashes,
        Func<FileRecord, Exception, bool>? onTransientSkip,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0 || !File.Exists(zipPath))
        {
            return;
        }

        await using var zipStream = new FileStream(
            zipPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            HashUtil.BufferSize,
            FileOptions.Asynchronous);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Update, leaveOpen: false);
        var buffer = new byte[HashUtil.BufferSize];
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            existing.Add(entry.FullName.Replace('\\', '/').Trim('/'));
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            var key = EntryName(file.RelativePath);
            if (existing.Contains(key))
            {
                continue;
            }

            pause.BeginFile(file.RelativePath, file.Size);
            try
            {
                await PackOneEntryAsync(zip, file, job, budget, pause, speed, buffer, hashes, cancellationToken)
                    .ConfigureAwait(false);
                existing.Add(key);
                log.Info(job.Id, name, $"Packed deferred {file.RelativePath}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (onTransientSkip?.Invoke(file, ex) == true)
            {
                // still locked
            }
            finally
            {
                pause.EndFile();
            }
        }
    }

    public static string ExtractRootFromZip(string zipPath)
    {
        var parent = Path.GetDirectoryName(zipPath);
        if (string.IsNullOrEmpty(parent))
        {
            parent = ".";
        }

        var name = Path.GetFileNameWithoutExtension(zipPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "received";
        }

        return Path.Combine(parent, name);
    }

    public static void DeleteTransport(string zipPath)
    {
        TryDelete(TempPath(zipPath));
        TryDelete(zipPath);
    }

    /// <summary>
    /// Stream zip entries directly to destRoot on the destination volume. Does not extract to %TEMP%.
    /// Leaves the transport zip in place — caller deletes it after extract completes.
    /// </summary>
    public static async Task ExtractAsync(
        string zipPath,
        string destRoot,
        IReadOnlyList<FileRecord>? files,
        OverwritePolicy overwrite,
        Job? job,
        BandwidthBudget? budget,
        PauseGate? pause,
        SpeedTracker? speed,
        IJobLog? log,
        string name,
        IProgress<JobProgress>? progress,
        bool cloud,
        Func<Job, PauseGate, IJobLog, string, IProgress<JobProgress>?, bool, CancellationToken, Task>? waitHours,
        Action<string>? onUnpacked,
        CancellationToken cancellationToken,
        Func<string, Exception, bool>? onTransientSkip = null,
        Func<string, Task>? onAfterFile = null)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("Transport zip is missing; cannot unpack.", zipPath);
        }

        Directory.CreateDirectory(destRoot);
        var destRootFull = Path.GetFullPath(destRoot);
        var zipFull = Path.GetFullPath(zipPath);

        await using var zipStream = new FileStream(
            zipPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            HashUtil.BufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (IsDirectoryEntry(entry))
            {
                continue;
            }

            entries[entry.FullName.Replace('\\', '/').Trim('/')] = entry;
        }

        var planned = new List<(string relative, string destPath, string? sourcePath, long size, DateTime lastWriteUtc)>();
        if (files is { Count: > 0 })
        {
            foreach (var file in files)
            {
                if (file.Status == FileCopyStatus.Skipped)
                {
                    continue;
                }

                planned.Add((file.RelativePath, file.DestPath, file.SourcePath, file.Size, file.LastWriteUtc));
            }
        }
        else
        {
            foreach (var (key, entry) in entries)
            {
                var destPath = PathNormalizer.Combine(destRootFull, key);
                var writeUtc = entry.LastWriteTime.UtcDateTime;
                planned.Add((key.Replace('/', '\\'), destPath, null, entry.Length, writeUtc));
            }
        }

        var buffer = new byte[HashUtil.BufferSize];
        var total = planned.Count;
        for (var i = 0; i < planned.Count; i++)
        {
            var (relative, destPath, sourcePath, size, lastWriteUtc) = planned[i];
            cancellationToken.ThrowIfCancellationRequested();
            if (pause is not null)
            {
                await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            }

            if (job is not null && waitHours is not null && pause is not null && log is not null)
            {
                await waitHours(job, pause, log, name, progress, cloud, cancellationToken).ConfigureAwait(false);
            }

            if (!IsInsideDest(destRootFull, destPath))
            {
                throw new InvalidDataException($"Zip entry would unpack outside the destination: {relative}");
            }

            if (PathsEqual(destPath, zipFull) ||
                destPath.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (job is not null)
            {
                Report(progress, job, name, cloud, $"Unpacking {relative}", relative, speed);
            }

            pause?.BeginFile(relative, size);

            var key = EntryName(relative);
            if (!entries.TryGetValue(key, out var entry))
            {
                pause?.EndFile();
                throw new FileNotFoundException($"Packed zip is missing entry {relative}.", zipPath);
            }

            if (ShouldSkipExtract(destPath, size, lastWriteUtc, overwrite, job?.Options))
            {
                if (job is not null && !string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
                {
                    FileMetadata.ApplyCopiedFile(sourcePath, destPath, job.Options, log, job.Id, name);
                }

                onUnpacked?.Invoke(relative);
                speed?.Add(size);
                pause?.EndFile();
                if (onAfterFile is not null)
                {
                    await onAfterFile(relative).ConfigureAwait(false);
                }

                continue;
            }

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            var temp = destPath + TempSuffix;
            TryDelete(temp);
            try
            {
                await using (var src = entry.Open())
                await using (var dst = new FileStream(
                                 temp,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 HashUtil.BufferSize,
                                 FileMetadata.SequentialIo(job?.Options)))
                {
                    int read;
                    while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                               .ConfigureAwait(false)) > 0)
                    {
                        if (pause is not null)
                        {
                            await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
                        }

                        if (budget is not null && job is not null)
                        {
                            await budget.ConsumeAsync(job.Id, job.Options.MaxBytesPerSecond, read, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        await dst.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        speed?.Add(read);
                        pause?.AddFileBytes(read);
                    }

                    await dst.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (File.Exists(destPath))
                {
                    File.Delete(destPath);
                }

                File.Move(temp, destPath);
                if (job is not null && !string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
                {
                    FileMetadata.ApplyCopiedFile(sourcePath, destPath, job.Options, log, job.Id, name);
                }
                else
                {
                    try
                    {
                        File.SetLastWriteTimeUtc(destPath, lastWriteUtc);
                    }
                    catch
                    {
                        // FAT / zip timestamp range
                    }
                }
            }
            catch (OperationCanceledException)
            {
                TryDelete(temp);
                pause?.EndFile();
                throw;
            }
            catch (Exception ex) when (onTransientSkip?.Invoke(relative, ex) == true)
            {
                TryDelete(temp);
                pause?.EndFile();
                continue;
            }
            catch
            {
                TryDelete(temp);
                pause?.EndFile();
                throw;
            }

            pause?.EndFile();
            onUnpacked?.Invoke(relative);
            if (log is not null && job is not null && (i == 0 || i + 1 == total || (i + 1) % 500 == 0))
            {
                log.Info(job.Id, name, $"Unpacked {i + 1}/{total}: {relative}");
            }

            if (onAfterFile is not null)
            {
                await onAfterFile(relative).ConfigureAwait(false);
            }
        }
    }

    public static async Task ExtractDirectAsync(
        string zipPath,
        string destRoot,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        log?.Invoke($"Unpacking {Path.GetFileName(zipPath)} into {destRoot}.");
        var skipped = new List<string>();
        await ExtractAsync(
            zipPath,
            destRoot,
            files: null,
            OverwritePolicy.SkipIfNewerOrEqual,
            job: null,
            budget: null,
            pause: null,
            speed: null,
            log: null,
            name: "catcher",
            progress: null,
            cloud: false,
            waitHours: null,
            onUnpacked: null,
            cancellationToken,
            onTransientSkip: (relative, ex) =>
            {
                if (!DeferredRetry.IsTransient(ex))
                {
                    return false;
                }

                log?.Invoke($"Deferred unpack {relative}: {ex.Message}");
                skipped.Add(relative);
                return true;
            }).ConfigureAwait(false);

        if (skipped.Count > 0)
        {
            log?.Invoke($"Retrying {skipped.Count} deferred unpack file(s) at end of pass.");
            await ExtractAsync(
                zipPath,
                destRoot,
                files: null,
                OverwritePolicy.SkipIfNewerOrEqual,
                job: null,
                budget: null,
                pause: null,
                speed: null,
                log: null,
                name: "catcher",
                progress: null,
                cloud: false,
                waitHours: null,
                onUnpacked: null,
                cancellationToken,
                onTransientSkip: (relative, ex) =>
                {
                    if (!DeferredRetry.IsTransient(ex))
                    {
                        return false;
                    }

                    log?.Invoke($"Deferred unpack still failing {relative}: {ex.Message}");
                    return true;
                }).ConfigureAwait(false);
        }
    }

    public static int Verify(
        Job job,
        JobJournal journal,
        CopyMapping mapping,
        IJobLog log,
        string name)
    {
        var issuesBefore = journal.IssueCount();
        var files = journal.GetFiles();
        var zipPath = ZipPath(mapping);

        if (!File.Exists(zipPath))
        {
            Add(journal, log, job, name, Path.GetFileName(zipPath), IssueKind.Missing,
                $"Packed zip is missing: {zipPath}");
            return journal.IssueCount() - issuesBefore;
        }

        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(zipPath);
        }
        catch (Exception ex)
        {
            Add(journal, log, job, name, Path.GetFileName(zipPath), IssueKind.CopyError,
                $"Could not open packed zip: {ex.Message}");
            return journal.IssueCount() - issuesBefore;
        }

        using (zip)
        {
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                if (IsDirectoryEntry(entry))
                {
                    continue;
                }

                entries[entry.FullName.Replace('\\', '/').Trim('/')] = entry;
            }

            foreach (var file in files)
            {
                if (file.Status is FileCopyStatus.Failed or FileCopyStatus.Deferred)
                {
                    Add(journal, log, job, name, file.RelativePath, IssueKind.CopyError,
                        file.Error ?? "Copy failed after retries.");
                    continue;
                }

                if (file.Status == FileCopyStatus.Skipped)
                {
                    continue;
                }

                var key = EntryName(file.RelativePath);
                if (!entries.TryGetValue(key, out var entry))
                {
                    Add(journal, log, job, name, file.RelativePath, IssueKind.Missing,
                        "File is missing from the packed zip.");
                    continue;
                }

                if (entry.Length != file.Size)
                {
                    Add(journal, log, job, name, file.RelativePath, IssueKind.SizeMismatch,
                        $"Size mismatch: source {file.Size} bytes, zip entry {entry.Length} bytes.");
                    continue;
                }

                if (job.Options.Verify == VerifyLevel.Thorough)
                {
                    try
                    {
                        using var stream = entry.Open();
                        var destHash = HashUtil.HashStream(stream);
                        var expected = file.Hash;
                        if (string.IsNullOrEmpty(expected) && File.Exists(file.SourcePath))
                        {
                            expected = HashUtil.HashFile(file.SourcePath);
                        }

                        if (!string.IsNullOrEmpty(expected) &&
                            !string.Equals(expected, destHash, StringComparison.OrdinalIgnoreCase))
                        {
                            Add(journal, log, job, name, file.RelativePath, IssueKind.HashMismatch,
                                $"Hash mismatch: source {expected}, zip {destHash}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Add(journal, log, job, name, file.RelativePath, IssueKind.HashMismatch, ex.Message);
                    }
                }
            }
        }

        try
        {
            var known = files.Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var record in SourceWalker.Walk(mapping, ExcludePaths(mapping), job.Options))
            {
                if (known.Contains(record.RelativePath))
                {
                    continue;
                }

                Add(journal, log, job, name, record.RelativePath, IssueKind.Missing,
                    "Source file was not in the job journal and is missing from the packed zip.");
            }
        }
        catch (Exception ex)
        {
            log.Error(job.Id, name, $"Inventory walk failed: {ex.Message}");
        }

        return journal.IssueCount() - issuesBefore;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || entry.Length == 0 && string.IsNullOrEmpty(Path.GetFileName(entry.FullName.TrimEnd('/', '\\')));

    private static void AddFolderPrefixes(HashSet<string> folders, string entryName)
    {
        var normalized = entryName.Replace('\\', '/').Trim('/');
        var slash = normalized.LastIndexOf('/');
        while (slash > 0)
        {
            folders.Add(normalized[..slash]);
            slash = normalized.LastIndexOf('/', slash - 1);
        }
    }

    private static bool ShouldSkipExtract(
        string destPath,
        long size,
        DateTime sourceTimeUtc,
        OverwritePolicy policy,
        JobOptions? options)
    {
        if (!File.Exists(destPath))
        {
            return false;
        }

        if (policy == OverwritePolicy.Always)
        {
            return false;
        }

        if (policy == OverwritePolicy.NeverIfExists)
        {
            return true;
        }

        var dest = new FileInfo(destPath);
        var tolerance = options is null
            ? CopyEngine.FatTimestampTolerance
            : FileMetadata.ComparisonTolerance(options);
        return dest.Length == size && dest.LastWriteTimeUtc + tolerance >= sourceTimeUtc;
    }

    private static bool IsInsideDest(string destRoot, string destPath)
    {
        try
        {
            var root = Path.GetFullPath(destRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(destPath);
            if (string.Equals(
                    full.TrimEnd('\\', '/'),
                    destRoot.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return (full + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                   PathNormalizer.IsUnder(full, destRoot);
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd('\\', '/'),
                Path.GetFullPath(right).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
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

    private static void Add(
        JobJournal journal,
        IJobLog log,
        Job job,
        string name,
        string relative,
        IssueKind kind,
        string message)
    {
        journal.AddIssue(new TransferIssue
        {
            RelativePath = relative,
            Kind = kind,
            Message = message
        });
        log.Error(job.Id, name, $"{kind}: {relative} — {message}");
    }

    private static void Report(
        IProgress<JobProgress>? progress,
        Job job,
        string name,
        bool cloud,
        string? message,
        string? current,
        SpeedTracker? speed)
    {
        progress?.Report(new JobProgress
        {
            JobId = job.Id,
            JobName = name,
            Status = job.Status,
            CurrentFile = current,
            Message = message,
            CloudDestination = cloud,
            BytesCopied = speed?.Bytes ?? 0,
            BytesPerSecond = speed?.BytesPerSecond ?? 0,
            IssueCount = job.IssueCount
        });
    }
}
