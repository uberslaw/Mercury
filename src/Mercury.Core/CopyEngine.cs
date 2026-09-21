using System.Diagnostics;
using System.IO.Hashing;

namespace Mercury;

public sealed class CopyEngine : ICopyEngine
{
    public static readonly TimeSpan FatTimestampTolerance = TimeSpan.FromSeconds(2);

    public async Task RunAsync(
        Job job,
        JobJournal journal,
        BandwidthBudget budget,
        PauseGate pause,
        IJobLog log,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        job.Options ??= new JobOptions();
        var name = string.IsNullOrWhiteSpace(job.Name) ? job.Id[..8] : job.Name;
        var catcher = job.Catcher;
        var cloud = catcher is null && CloudPath.LooksLikeCloudFolder(job.DestinationPath);
        if (cloud)
        {
            log.Info(job.Id, name, "Destination looks like a cloud-synced folder. Mercury verifies the local copy, not the cloud upload.");
        }

        if (catcher is not null)
        {
            log.Info(job.Id, name,
                $"Catcher destination {CatcherCrypto.FormatBaseUrl(catcher.PublicHost, catcher.PublicPort)}. TLS fingerprint pin is required. HTTPS zip-pack push — per-file resume is not in v1.");
        }

        var destForShape = catcher is null ? job.DestinationPath : journal.Directory;
        var mapping = CopyShape.Resolve(job.SourcePath, destForShape, job.Options.IncludeSourceFolderName);
        job.SourceKind = mapping.Kind;
        job.VolumeSerial ??= VolumeInfo.GetSerial(job.SourcePath);

        if (job.StartedUtc is null)
        {
            TransferRundown.MarkStarted(job);
        }

        if (catcher is not null && !mapping.SingleFile)
        {
            job.Options.PackAsZip = true;
        }

        var pack = catcher is not null ? !mapping.SingleFile : ZipPack.Applies(job, mapping);
        var totals = journal.Totals();
        var hasJournal = totals.Files > 0;
        var stages = CopyPipeline.For(job, hasJournal, pack);
        var reporter = new JobProgressReporter(job, name, cloud, stages, progress, log);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = reporter.HeartbeatAsync(heartbeatCts.Token);
        var dirtyPulse = PulseDirtyAsync(job, journal, pause, heartbeatCts.Token);
        UnbufferedIoSession? io = null;
        try
        {
            reporter.Enter(CopyStageKind.PreparingDestination, JobStatus.Preparing,
                catcher is null ? "Preparing destination…" : "Preparing Catcher send…");
            journal.SaveJob(job);
            JobHeartbeat.Write(journal, job.Id, 0, null);
            await PreflightAsync(job, mapping, log, name, cancellationToken).ConfigureAwait(false);

            if (!hasJournal)
            {
                reporter.Enter(CopyStageKind.EnumeratingSource, JobStatus.Enumerating, "Enumerating source…");
                log.Info(job.Id, name, "Enumerating source files.");
                var exclude = new List<string>();
                if (pack)
                {
                    exclude.AddRange(ZipPack.ExcludePaths(mapping));
                }

                var found = 0;
                var foundBytes = 0L;
                var lastPulse = Stopwatch.GetTimestamp();
                var inventory = new PayloadInventory();
                var peek = new MagicPeekBudget();
                var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var record in SourceWalker.Walk(mapping, exclude, job.Options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var kind = FileClassifier.Classify(record.SourcePath, record.Size, peek);
                    record.PayloadKind = kind;
                    inventory.Add(kind, record.Size);
                    ZipPack.NoteFolders(folders, record.RelativePath);
                    journal.UpsertFile(record);
                    found++;
                    foundBytes += record.Size;
                    var elapsed = (Stopwatch.GetTimestamp() - lastPulse) / (double)Stopwatch.Frequency;
                    if (elapsed >= 1)
                    {
                        job.SourceFiles = found;
                        reporter.Update(
                            $"Enumerating source… {found} files",
                            filesTotal: found,
                            bytesTotal: foundBytes,
                            typeSummary: inventory.FormatSummary());
                        lastPulse = Stopwatch.GetTimestamp();
                    }
                }

                totals = journal.Totals();
                job.SourceFiles = totals.Files;
                job.SourceFolders = folders.Count;
                log.Info(job.Id, name, $"Found {totals.Files} files ({ByteFormatter.ToString(totals.Bytes)}).");
                var typeSummary = inventory.FormatSummary();
                if (!string.IsNullOrWhiteSpace(typeSummary))
                {
                    log.Info(job.Id, name, typeSummary);
                }

                log.Info(job.Id, name, FileMetadata.DescribeFlags(job.Options));
                reporter.Update(
                    $"Enumerating source… {totals.Files} files",
                    filesTotal: totals.Files,
                    bytesTotal: totals.Bytes,
                    typeSummary: typeSummary);

                var ioFromWalk = UnbufferedIoSession.Create(job.Options, inventory, budget);
                LogIoPlan(job, mapping, ioFromWalk, log, name);
                io = ioFromWalk;

                if (catcher is null)
                {
                    reporter.Enter(CopyStageKind.CheckingDestinationSpace, JobStatus.Enumerating, "Checking destination space…");
                    var available = FreeSpace.GetAvailableBytes(mapping.DestRoot);
                    if (available is not null)
                    {
                        log.Info(job.Id, name, $"Destination free space: {ByteFormatter.ToString(available.Value)}.");
                    }

                    if (pack && catcher is null)
                    {
                        log.Info(job.Id, name,
                            "Pack as zip writes a transport zip then unpacks at dest (both exist until unpack finishes).");
                        FreeSpace.CheckOrWarn(job, totals.Bytes * 2, available, log, name);
                    }
                    else
                    {
                        FreeSpace.CheckOrWarn(job, totals.Bytes, available, log, name);
                    }
                }
            }
            else
            {
                log.Info(job.Id, name, $"Resuming journal: {totals.DoneFiles}/{totals.Files} files already done.");
                if (job.ScanSourceOnResume)
                {
                    reporter.Enter(CopyStageKind.EnumeratingSource, JobStatus.Enumerating, "Checking source for changes…");
                    log.Info(job.Id, name, "Checking source for new, changed, or deleted files vs the journal.");
                    var exclude = new List<string>();
                    if (pack)
                    {
                        exclude.AddRange(ZipPack.ExcludePaths(mapping));
                    }

                    var scan = JournalReconcile.Scan(
                        mapping,
                        journal,
                        job.Options,
                        exclude,
                        (seen, file) => reporter.Update(
                            $"Checking source for changes… {seen} files",
                            file,
                            filesTotal: Math.Max(seen, totals.Files),
                            bytesTotal: totals.Bytes),
                        cancellationToken);
                    log.Info(job.Id, name,
                        $"Source check: {scan.Added} new, {scan.Changed} changed, {scan.Removed} gone, {scan.Unchanged} unchanged.");
                    totals = journal.Totals();
                    job.SourceFiles = totals.Files;
                    job.ScanSourceOnResume = false;
                    journal.SaveJob(job);
                }
                log.Info(job.Id, name, FileMetadata.DescribeFlags(job.Options));
                var inventory = PayloadInventory.FromFiles(journal.GetFiles());
                var typeSummary = inventory.FormatSummary();
                if (!string.IsNullOrWhiteSpace(typeSummary))
                {
                    log.Info(job.Id, name, typeSummary);
                }

                reporter.Update(
                    filesCopied: totals.DoneFiles,
                    filesTotal: totals.Files,
                    bytesCopied: totals.DoneBytes,
                    bytesTotal: totals.Bytes,
                    typeSummary: typeSummary);
                io = UnbufferedIoSession.Create(job.Options, inventory, budget);
                LogIoPlan(job, mapping, io, log, name);
            }

            io ??= UnbufferedIoSession.Create(job.Options, new PayloadInventory(), budget);

            if (pack && catcher is null)
            {
                var listed = journal.GetFiles();
                if (!NeedsTransportZip(job, listed))
                {
                    pack = false;
                    log.Info(job.Id, name,
                        $"All {listed.Count} file(s) are already compressed (video, photos, audio, archives, disk images) — copying as-is, no transport zip.");
                }

                reporter.ReplaceStages(CopyPipeline.For(job, hasJournalFiles: true, pack));
            }

            if (job.Options.DryRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                log.Info(job.Id, name, "Dry run — no files will be written.");
                job.Status = JobStatus.Completed;
                job.ResultMessage = $"Dry run: {totals.Files} files, {ByteFormatter.ToString(totals.Bytes)}.";
                JobHeartbeat.Clear(journal);
                reporter.Update(job.ResultMessage);
                return;
            }

            if (catcher is null)
            {
                Directory.CreateDirectory(mapping.DestRoot);
            }

            var speed = new SpeedTracker();
            if (pack)
            {
                reporter.Enter(CopyStageKind.Transferring, JobStatus.Copying, "Packing…");
                journal.SaveJob(job);
                await PackWithRetriesAsync(job, mapping, journal, budget, pause, log, name, reporter, cloud, speed, cancellationToken, io)
                    .ConfigureAwait(false);
                var zipPath = ZipPack.ZipPath(mapping);
                if (catcher is null && File.Exists(zipPath))
                {
                    reporter.Enter(CopyStageKind.Unpacking, JobStatus.Copying, "Unpacking…");
                    journal.SaveJob(job);
                    await UnpackWithRetriesAsync(
                            job, mapping, journal, budget, pause, log, name, reporter, cloud, speed, cancellationToken, io)
                        .ConfigureAwait(false);
                }
                else if (catcher is null)
                {
                    log.Info(job.Id, name, "No transport zip to unpack — already-compressed files were copied as-is.");
                }
            }
            else if (catcher is null)
            {
                reporter.Enter(CopyStageKind.Transferring, JobStatus.Copying, "Copying…");
                journal.SaveJob(job);
                var deferred = new DeferredRetrySession(job, journal, log, name);
                var pending = journal.GetFiles()
                    .Where(f => f.Status is FileCopyStatus.Pending or FileCopyStatus.Failed or FileCopyStatus.Deferred)
                    .ToList();
                var fileCount = Math.Max(pending.Count, totals.Files);
                var totalBytes = totals.Bytes;
                foreach (var file in pending.ToList())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
                    await WaitForHoursAsync(job, pause, log, name, reporter, cloud, cancellationToken).ConfigureAwait(false);

                    reporter.Update($"Copying {file.RelativePath}", file.RelativePath, speed);

                    try
                    {
                        if (ShouldSkip(file, job.Options))
                        {
                            TryJournal(journal, () => journal.MarkSkipped(file.RelativePath, "Destination is newer or equal"), log, job.Id, name);
                            log.Info(job.Id, name, $"Skip {file.RelativePath} (dest newer or equal)");
                            speed.Add(file.Size);
                            continue;
                        }

                        pause.BeginFile(file.RelativePath, file.Size);
                        var copied = await CopyWithRetriesAsync(job, file, journal, budget, pause, log, name, speed, cancellationToken, io)
                            .ConfigureAwait(false);
                        pause.EndFile();
                        if (copied.ok)
                        {
                            if (!TryJournal(journal, () => journal.MarkCopied(file.RelativePath, copied.hash), log, job.Id, name))
                            {
                                deferred.Defer(file, "Journal error after copy — deferred for retry");
                            }
                            else
                            {
                                deferred.Remove(file.RelativePath);
                                log.Info(job.Id, name, $"Copied {file.RelativePath} ({ByteFormatter.ToString(file.Size)})");
                            }
                        }
                        else
                        {
                            var error = copied.error ?? "Copy failed after retries";
                            if (copied.transient || ProgressHeader.IsExceptionDump(error))
                            {
                                deferred.Defer(file, error);
                            }
                            else
                            {
                                if (!TryJournal(journal, () =>
                                    {
                                        journal.MarkFailed(file.RelativePath, error, job.Options.RetryCount);
                                        journal.AddIssue(new TransferIssue
                                        {
                                            RelativePath = file.RelativePath,
                                            Kind = IssueKind.CopyError,
                                            Message = error
                                        });
                                    }, log, job.Id, name))
                                {
                                    deferred.Defer(file, error);
                                }
                                else
                                {
                                    log.Error(job.Id, name, $"Failed {file.RelativePath}: {error}");
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        pause.EndFile();
                        var error = ProgressHeader.DescribeFileError(ex);
                        log.Error(job.Id, name, $"File error {file.RelativePath}: {error}");
                        deferred.Defer(file, error);
                    }

                    try
                    {
                        await PauseAfterFileIfRequestedAsync(job, journal, pause, log, name, file.RelativePath, reporter, cancellationToken)
                            .ConfigureAwait(false);
                        await RetryDeferredCopyAsync(
                                job, journal, deferred, budget, pause, log, name, reporter, speed, fileCount, totalBytes,
                                afterFile: true, endOfPass: false, cancellationToken, io)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        log.Error(job.Id, name, "Continuing after file-level error: " + ProgressHeader.DescribeFileError(ex));
                    }
                }

                await RetryDeferredCopyAsync(
                        job, journal, deferred, budget, pause, log, name, reporter, speed, fileCount, totalBytes,
                        afterFile: false, endOfPass: true, cancellationToken, io)
                    .ConfigureAwait(false);
                deferred.FinalizeFailures();
            }

            if (catcher is null)
            {
                FileMetadata.FinishDestination(job, mapping, journal, log, name);
            }

            if (catcher is not null)
            {
                reporter.Enter(CopyStageKind.Pushing, JobStatus.Copying, "Sending over HTTPS…");
                journal.SaveJob(job);
                await PushCatcherAsync(
                    job,
                    catcher,
                    mapping,
                    pack,
                    journal,
                    budget,
                    pause,
                    log,
                    name,
                    reporter,
                    speed,
                    cancellationToken).ConfigureAwait(false);
            }

            reporter.Enter(CopyStageKind.Verifying, JobStatus.Verifying, "Verifying…");
            journal.SaveJob(job);
            log.Info(job.Id, name, $"Verify started ({job.Options.Verify}).");

            var issues = catcher is not null && pack
                ? ZipPack.Verify(job, journal, mapping, log, name)
                : catcher is not null && !pack
                    ? 0
                    : Verifier.Verify(job, journal, mapping, log, name);
            job.IssueCount = issues;
            if (issues == 0)
            {
                job.Status = JobStatus.Completed;
                if (catcher is not null)
                {
                    job.ResultMessage = pack
                        ? $"Verified complete. Catcher unpacked the transfer at {CatcherCrypto.FormatBaseUrl(catcher.PublicHost, catcher.PublicPort)}."
                        : $"Verified complete. Sent file to Catcher {CatcherCrypto.FormatBaseUrl(catcher.PublicHost, catcher.PublicPort)}.";
                }
                else if (pack)
                {
                    job.ResultMessage =
                        $"Verified complete. Unpacked {totals.Files} files to {mapping.DestRoot}.";
                }
                else if (cloud)
                {
                    job.ResultMessage = "Verified complete (local destination). Cloud upload is the sync client’s job.";
                }
                else
                {
                    job.ResultMessage = "Verified complete.";
                }

                log.Info(job.Id, name, job.ResultMessage);
            }
            else
            {
                job.Status = JobStatus.Incomplete;
                job.ResultMessage = $"Incomplete — {issues} issue(s). Open the log for details.";
                log.Error(job.Id, name, job.ResultMessage);
            }

            reporter.Update(job.ResultMessage);
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }

            try
            {
                await dirtyPulse.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }
    }

    private static async Task PushCatcherAsync(
        Job job,
        CatcherTarget catcher,
        CopyMapping mapping,
        bool pack,
        JobJournal journal,
        BandwidthBudget budget,
        PauseGate pause,
        IJobLog log,
        string name,
        JobProgressReporter reporter,
        SpeedTracker speed,
        CancellationToken cancellationToken)
    {
        await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
        var passphrase = CatcherSession.Require(catcher.TemplateId);
        log.Info(job.Id, name, "Checking Catcher HTTPS listener (TLS fingerprint pin)…");
        await CatcherClient.ReadyAsync(catcher, passphrase, cancellationToken).ConfigureAwait(false);

        var payloadPath = pack ? ZipPack.ZipPath(mapping) : job.SourcePath;
        if (!File.Exists(payloadPath))
        {
            throw new FileNotFoundException("Nothing to send to Catcher.", payloadPath);
        }

        var fileName = CatcherCrypto.SafeFileName(Path.GetFileName(payloadPath));
        var sha = CatcherCrypto.Sha256File(payloadPath);
        log.Info(job.Id, name, $"Sending {fileName} ({ByteFormatter.ToString(new FileInfo(payloadPath).Length)}) over HTTPS.");
        reporter.Update($"Sending {fileName} over HTTPS…", fileName, speed);

        var result = await CatcherClient.PushAsync(
            catcher,
            passphrase,
            payloadPath,
            fileName,
            sha,
            budget,
            job.Id,
            job.Options.MaxBytesPerSecond,
            bytes =>
            {
                speed.Add(bytes);
                reporter.Update($"Sending {fileName} over HTTPS…", fileName, speed);
            },
            cancellationToken,
            unpack: pack).ConfigureAwait(false);

        if (!pack)
        {
            journal.MarkAllCopied();
        }

        var where = string.IsNullOrWhiteSpace(result.Path) ? "Catcher receive folder" : result.Path;
        log.Info(job.Id, name,
            result.AlreadyReceived
                ? $"Catcher already had this payload (same SHA-256) at {where}."
                : pack
                    ? $"Catcher received the zip and unpacked it to {where}."
                    : $"Catcher stored {fileName} ({ByteFormatter.ToString(result.ReceivedBytes)}).");
        reporter.Update($"Catcher received {fileName}", fileName, speed);
    }

    private static async Task PreflightAsync(
        Job job,
        CopyMapping mapping,
        IJobLog log,
        string name,
        CancellationToken cancellationToken)
    {
        var timeout = PathProbe.TimeoutFor(job.SourcePath);
        if (!await PathProbe.ExistsAsync(job.SourcePath, timeout, cancellationToken).ConfigureAwait(false))
        {
            throw new DirectoryNotFoundException($"Source not found: {job.SourcePath}");
        }

        var probeRoot = mapping.DestRoot;
        if (ZipPack.Applies(job, mapping))
        {
            probeRoot = Path.GetDirectoryName(ZipPack.ZipPath(mapping)) ?? mapping.DestRoot;
        }

        Directory.CreateDirectory(probeRoot);

        var probe = Path.Combine(probeRoot, $".mercury-write-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(probe, "ok", cancellationToken).ConfigureAwait(false);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new IOException($"Destination is not writable: {probeRoot}. {ex.Message}", ex);
        }

        if (!job.Options.DryRun)
        {
            var needed = 0L;
            try
            {
                if (mapping.SingleFile)
                {
                    needed = new FileInfo(job.SourcePath).Length;
                }
            }
            catch
            {
                needed = 0;
            }

            var available = FreeSpace.GetAvailableBytes(mapping.DestRoot);
            if (available is not null)
            {
                log.Info(job.Id, name, $"Destination free space: {ByteFormatter.ToString(available.Value)}.");
            }

            FreeSpace.CheckOrWarn(job, needed, available, log, name);
        }
    }

    private static bool ShouldSkip(FileRecord file, JobOptions options)
    {
        if (!File.Exists(file.DestPath))
        {
            return false;
        }

        if (options.Overwrite == OverwritePolicy.Always)
        {
            return false;
        }

        if (options.Overwrite == OverwritePolicy.NeverIfExists)
        {
            return true;
        }

        var dest = new FileInfo(file.DestPath);
        var destTime = dest.LastWriteTimeUtc;
        var sourceTime = file.LastWriteUtc;
        if (dest.Length == file.Size && destTime + FileMetadata.ComparisonTolerance(options) >= sourceTime)
        {
            return true;
        }

        return false;
    }

    private static bool CopyBesideZip(Job job, FileRecord file, MagicPeekBudget? peek = null)
    {
        if (job.Catcher is not null)
        {
            return false;
        }

        if (job.Options.CopySymbolicLinksAsLinks && FileMetadata.IsSymlinkFile(file.SourcePath))
        {
            return true;
        }

        if (!job.Options.PackAsZip || !job.Options.SkipCompressedWhenPacking)
        {
            return false;
        }

        return CompressedMedia.IsAlreadyCompressed(file, peek);
    }

    private static bool NeedsTransportZip(Job job, IReadOnlyList<FileRecord> files)
    {
        if (!job.Options.PackAsZip)
        {
            return false;
        }

        var peek = MagicPeekBudget.ForPackSkip();
        return files.Any(f => f.Status != FileCopyStatus.Skipped && !CopyBesideZip(job, f, peek));
    }

    private static async Task PauseAfterFileIfRequestedAsync(
        Job job,
        JobJournal journal,
        PauseGate pause,
        IJobLog log,
        string name,
        string relativePath,
        JobProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        if (!pause.TryApplyPauseAfterFile())
        {
            return;
        }

        job.Status = JobStatus.Paused;
        TryJournal(journal, () => journal.SaveJob(job), log, job.Id, name);
        log.Info(job.Id, name, $"Paused after completing {relativePath}.");
        reporter.Update($"Paused after completing {relativePath}");
        await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
        if (job.Status == JobStatus.Paused)
        {
            job.Status = JobStatus.Copying;
            TryJournal(journal, () => journal.SaveJob(job), log, job.Id, name);
            log.Info(job.Id, name, "Resumed.");
        }
    }

    private static async Task RetryDeferredCopyAsync(
        Job job,
        JobJournal journal,
        DeferredRetrySession deferred,
        BandwidthBudget budget,
        PauseGate pause,
        IJobLog log,
        string name,
        JobProgressReporter reporter,
        SpeedTracker speed,
        int fileCount,
        long totalBytes,
        bool afterFile,
        bool endOfPass,
        CancellationToken cancellationToken,
        UnbufferedIoSession? io = null)
    {
        var largest = deferred.Items.Count == 0 ? 0 : deferred.Items.Max(f => f.Size);
        if (!deferred.ShouldRetry(speed.Bytes, totalBytes, fileCount, afterFile, endOfPass, largest))
        {
            return;
        }

        var reason = deferred.Reason(speed.Bytes, totalBytes, afterFile, endOfPass);
        deferred.NoteRetry(speed.Bytes, totalBytes, endOfPass);
        var snapshot = deferred.Items.ToList();
        log.Info(job.Id, name, $"Retrying deferred files ({reason}, {snapshot.Count} file(s)).");
        reporter.Update("Retrying deferred files");

        foreach (var file in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            reporter.Update($"Retrying deferred {file.RelativePath}", file.RelativePath, speed);
            try
            {
            pause.BeginFile(file.RelativePath, file.Size);
            var copied = await CopyWithRetriesAsync(job, file, journal, budget, pause, log, name, speed, cancellationToken, io)
                .ConfigureAwait(false);
            pause.EndFile();
            if (copied.ok)
            {
                if (!TryJournal(journal, () => journal.MarkCopied(file.RelativePath, copied.hash), log, job.Id, name))
                {
                    deferred.Keep(file, "Journal error after copy — deferred for retry");
                }
                else
                {
                    deferred.Remove(file.RelativePath);
                    log.Info(job.Id, name, $"Deferred retry succeeded {file.RelativePath} ({ByteFormatter.ToString(file.Size)})");
                }
            }
            else
            {
                var error = copied.error ?? "Copy failed after retries";
                log.Error(job.Id, name, $"Deferred retry still failing {file.RelativePath}: {error}");
                if (copied.transient || ProgressHeader.IsExceptionDump(error))
                {
                    deferred.Keep(file, error);
                }
                else
                {
                    deferred.Remove(file.RelativePath);
                    if (!TryJournal(journal, () =>
                        {
                            journal.MarkFailed(file.RelativePath, error, job.Options.RetryCount);
                            journal.AddIssue(new TransferIssue
                            {
                                RelativePath = file.RelativePath,
                                Kind = IssueKind.CopyError,
                                Message = error
                            });
                        }, log, job.Id, name))
                    {
                        deferred.Keep(file, error);
                    }
                }
            }

            await PauseAfterFileIfRequestedAsync(job, journal, pause, log, name, file.RelativePath, reporter, cancellationToken)
                .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                pause.EndFile();
                var error = ProgressHeader.DescribeFileError(ex);
                log.Error(job.Id, name, $"File error {file.RelativePath}: {error}");
                deferred.Keep(file, error);
            }
        }
    }

    private static async Task<(bool ok, string? hash, string? error, bool transient)> CopyWithRetriesAsync(
        Job job,
        FileRecord file,
        JobJournal journal,
        BandwidthBudget budget,
        PauseGate pause,
        IJobLog log,
        string name,
        SpeedTracker speed,
        CancellationToken cancellationToken,
        UnbufferedIoSession? io = null)
    {
        var attempts = Math.Max(1, job.Options.RetryCount + 1);
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var hash = await CopyOneAsync(
                        job, file, budget, pause, speed, cancellationToken, log, name, io,
                        onCopied: n =>
                        {
                            try
                            {
                                journal.SetCopiedBytes(file.RelativePath, n);
                            }
                            catch (Exception ex) when (JobJournal.IsJournalFault(ex))
                            {
                                // never fail a file copy because heartbeat/journal progress could not flush
                            }
                        })
                    .ConfigureAwait(false);
                return (true, hash, null, false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                log.Error(job.Id, name, $"Retry {i + 1}/{attempts} {file.RelativePath}: {ex.Message}");
                if (i + 1 < attempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, job.Options.RetryWaitSeconds)), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        return (false, null, last?.Message ?? "Copy failed", last is not null && DeferredRetry.IsTransient(last));
    }

    private static async Task PackWithRetriesAsync(
        Job job,
        CopyMapping mapping,
        JobJournal journal,
        BandwidthBudget budget,
        PauseGate pause,
        IJobLog log,
        string name,
        IProgress<JobProgress>? progress,
        bool cloud,
        SpeedTracker speed,
        CancellationToken cancellationToken,
        UnbufferedIoSession? io = null)
    {
        var zipPath = ZipPack.ZipPath(mapping);
        var files = journal.GetFiles().Where(f => f.Status != FileCopyStatus.Skipped).ToList();
        var pending = files.Where(f => f.Status is FileCopyStatus.Pending or FileCopyStatus.Failed).ToList();
        if (pending.Count == 0 && !File.Exists(zipPath))
        {
            var unpackIncomplete = files.Any(f =>
                f.Status is FileCopyStatus.Copied or FileCopyStatus.Pending or FileCopyStatus.Failed);
            if (unpackIncomplete)
            {
                pending = files;
            }
        }

        var catcherPack = job.Catcher is not null;
        var peek = MagicPeekBudget.ForPackSkip();
        var packable = catcherPack
            ? pending
            : pending.Where(f => !CopyBesideZip(job, f, peek)).ToList();
        var loose = catcherPack
            ? []
            : pending.Where(f => CopyBesideZip(job, f, peek)).ToList();

        if (pending.Count == 0)
        {
            log.Info(job.Id, name, $"Pack already recorded; verifying {zipPath}");
            return;
        }

        if (ShouldSkipZip(zipPath, files, job.Options))
        {
            log.Info(job.Id, name, $"Skip pack — zip already exists: {zipPath}");
            foreach (var file in packable)
            {
                journal.MarkCopied(file.RelativePath, file.Hash);
            }

            await CopyAlreadyCompressedAsync(
                    job, journal, budget, pause, log, name, progress, cloud, speed, loose, cancellationToken, io)
                .ConfigureAwait(false);
            return;
        }

        var deferred = new DeferredRetrySession(job, journal, log, name);
        var reporter = progress as JobProgressReporter;
        reporter?.Update(speed: speed);
        if (packable.Count == 0)
        {
            log.Info(job.Id, name,
                $"All {loose.Count} file(s) are already compressed — copying as-is (no transport zip).");
            await CopyAlreadyCompressedAsync(
                    job, journal, budget, pause, log, name, progress, cloud, speed, loose, cancellationToken, io)
                .ConfigureAwait(false);
            return;
        }

        if (loose.Count > 0)
        {
            log.Info(job.Id, name,
                $"Copying {loose.Count} already-compressed file(s) as-is. Packing {packable.Count} compressible file(s) into the transport zip.");
        }

        log.Info(job.Id, name,
            $"Packing {packable.Count} files into {zipPath} (stored zip, no compression — transport only; dest is unpacked after).");

        var totalBytes = packable.Sum(f => f.Size);
        var fileCount = packable.Count;
        var attempts = Math.Max(1, job.Options.RetryCount + 1);
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var hashes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                await ZipPack.PackAsync(
                    zipPath,
                    packable,
                    job,
                    budget,
                    pause,
                    speed,
                    log,
                    name,
                    progress,
                    cloud,
                    WaitForHoursAsync,
                    hashes,
                    cancellationToken,
                    onTransientSkip: (file, ex) =>
                    {
                        if (!DeferredRetry.IsTransient(ex))
                        {
                            return false;
                        }

                        deferred.Defer(file, ex.Message);
                        return true;
                    },
                    onAfterFile: async rel =>
                    {
                        if (reporter is not null)
                        {
                            await PauseAfterFileIfRequestedAsync(job, journal, pause, log, name, rel, reporter, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    },
                    onRetryDeferred: async (zip, buffer) =>
                    {
                        if (!deferred.HasItems)
                        {
                            return;
                        }

                        var largest = deferred.Items.Max(f => f.Size);
                        if (!deferred.ShouldRetry(speed.Bytes, totalBytes, fileCount, afterFile: true, endOfPass: false, largest))
                        {
                            return;
                        }

                        var reason = deferred.Reason(speed.Bytes, totalBytes, afterFile: true, endOfPass: false);
                        deferred.NoteRetry(speed.Bytes, totalBytes, endOfPass: false);
                        log.Info(job.Id, name, $"Retrying deferred files ({reason}, {deferred.Items.Count} file(s)).");
                        reporter?.Update("Retrying deferred files");
                        foreach (var file in deferred.Items.ToList())
                        {
                            try
                            {
                                await ZipPack.PackOneEntryAsync(zip, file, job, budget, pause, speed, buffer, hashes, cancellationToken)
                                    .ConfigureAwait(false);
                                deferred.Remove(file.RelativePath);
                                log.Info(job.Id, name, $"Deferred retry succeeded {file.RelativePath}");
                            }
                            catch (Exception ex) when (DeferredRetry.IsTransient(ex))
                            {
                                deferred.Keep(file, ex.Message);
                                log.Error(job.Id, name, $"Deferred retry still failing {file.RelativePath}: {ex.Message}");
                            }
                        }
                    }).ConfigureAwait(false);

                foreach (var (rel, hash) in hashes)
                {
                    journal.MarkCopied(rel, hash);
                    deferred.Remove(rel);
                }

                if (deferred.HasItems)
                {
                    log.Info(job.Id, name, $"Retrying deferred files (end of pass, {deferred.Items.Count} file(s)).");
                    reporter?.Update("Retrying deferred files");
                    deferred.NoteRetry(speed.Bytes, totalBytes, endOfPass: true);
                    var still = deferred.Items.ToList();
                    await ZipPack.AppendEntriesAsync(
                        zipPath,
                        still,
                        job,
                        budget,
                        pause,
                        speed,
                        log,
                        name,
                        hashes,
                        (file, ex) =>
                        {
                            if (!DeferredRetry.IsTransient(ex))
                            {
                                return false;
                            }

                            deferred.Keep(file, ex.Message);
                            return true;
                        },
                        cancellationToken).ConfigureAwait(false);
                    foreach (var (rel, hash) in hashes)
                    {
                        journal.MarkCopied(rel, hash);
                        deferred.Remove(rel);
                    }
                }

                deferred.FinalizeFailures();
                log.Info(job.Id, name, $"Packed {packable.Count} files into {zipPath}");
                await CopyAlreadyCompressedAsync(
                        job, journal, budget, pause, log, name, progress, cloud, speed, loose, cancellationToken, io)
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                log.Error(job.Id, name, $"Pack retry {i + 1}/{attempts}: {ex.Message}");
                if (i + 1 < attempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, job.Options.RetryWaitSeconds)), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new IOException(last?.Message ?? "Pack failed after retries", last);
    }

    private static async Task CopyAlreadyCompressedAsync(
        Job job,
        JobJournal journal,
        BandwidthBudget budget,
        PauseGate pause,
        IJobLog log,
        string name,
        IProgress<JobProgress>? progress,
        bool cloud,
        SpeedTracker speed,
        IReadOnlyList<FileRecord> files,
        CancellationToken cancellationToken,
        UnbufferedIoSession? io = null)
    {
        if (files.Count == 0)
        {
            return;
        }

        var reporter = progress as JobProgressReporter;
        reporter?.Update(speed: speed);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            await WaitForHoursAsync(job, pause, log, name, reporter, cloud, cancellationToken)
                .ConfigureAwait(false);
            if (file.Status is FileCopyStatus.Unpacked or FileCopyStatus.Skipped)
            {
                continue;
            }

            reporter?.Update($"Copying {file.RelativePath} (already compressed)", file.RelativePath, speed);
            try
            {
                if (ShouldSkip(file, job.Options))
                {
                    TryJournal(journal, () => journal.MarkSkipped(file.RelativePath, "Destination is newer or equal"), log, job.Id, name);
                    log.Info(job.Id, name, $"Skip {file.RelativePath} (already compressed, dest newer or equal)");
                    continue;
                }

                pause.BeginFile(file.RelativePath, file.Size);
                var copied = await CopyWithRetriesAsync(job, file, journal, budget, pause, log, name, speed, cancellationToken, io)
                    .ConfigureAwait(false);
                pause.EndFile();
            if (copied.ok)
            {
                if (!TryJournal(journal, () =>
                    {
                        journal.MarkCopied(file.RelativePath, copied.hash);
                        journal.MarkUnpacked(file.RelativePath);
                    }, log, job.Id, name))
                {
                    log.Error(job.Id, name, $"Copied {file.RelativePath} but could not journal it — continuing.");
                }
                else
                {
                    log.Info(job.Id, name, $"Copied {file.RelativePath} as-is (already compressed, {ByteFormatter.ToString(file.Size)})");
                }
            }
            else
            {
                var error = copied.error ?? "Copy failed after retries";
                if (ProgressHeader.IsExceptionDump(error) || DeferredRetry.IsTransient(new IOException(error)))
                {
                    log.Error(job.Id, name, $"Deferred {file.RelativePath}: {error}");
                }

                if (!TryJournal(journal, () =>
                    {
                        journal.MarkFailed(file.RelativePath, error, job.Options.RetryCount);
                        journal.AddIssue(new TransferIssue
                        {
                            RelativePath = file.RelativePath,
                            Kind = IssueKind.CopyError,
                            Message = error
                        });
                    }, log, job.Id, name))
                {
                    log.Error(job.Id, name, $"Failed {file.RelativePath} (journal unavailable): {error}");
                }
                else
                {
                    log.Error(job.Id, name, $"Failed {file.RelativePath}: {error}");
                }
            }

            if (reporter is not null)
            {
                await PauseAfterFileIfRequestedAsync(job, journal, pause, log, name, file.RelativePath, reporter, cancellationToken)
                    .ConfigureAwait(false);
            }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                pause.EndFile();
                log.Error(job.Id, name, $"File error {file.RelativePath}: {ProgressHeader.DescribeFileError(ex)}");
            }
        }
    }

    private static async Task UnpackWithRetriesAsync(
        Job job,
        CopyMapping mapping,
        JobJournal journal,
        BandwidthBudget budget,
        PauseGate pause,
        IJobLog log,
        string name,
        IProgress<JobProgress>? progress,
        bool cloud,
        SpeedTracker speed,
        CancellationToken cancellationToken,
        UnbufferedIoSession? io = null)
    {
        var zipPath = ZipPack.ZipPath(mapping);
        var files = journal.GetFiles().Where(f => f.Status != FileCopyStatus.Skipped).ToList();
        var pending = files.Where(f => f.Status != FileCopyStatus.Unpacked).ToList();
        var catcherPack = job.Catcher is not null;
        var zipPending = catcherPack
            ? pending
            : pending.Where(f => !CopyBesideZip(job, f)).ToList();
        var loosePending = catcherPack
            ? []
            : pending.Where(f => CopyBesideZip(job, f)).ToList();
        if (zipPending.Count == 0)
        {
            await CopyAlreadyCompressedAsync(
                    job, journal, budget, pause, log, name, progress, cloud, speed, loosePending, cancellationToken, io)
                .ConfigureAwait(false);
            ZipPack.DeleteTransport(zipPath);
            log.Info(job.Id, name, $"Unpack already recorded; removed transport zip if it was still at {zipPath}.");
            return;
        }

        if (!File.Exists(zipPath))
        {
            log.Info(job.Id, name, $"Transport zip missing at {zipPath}; packing again before unpack.");
            await PackWithRetriesAsync(job, mapping, journal, budget, pause, log, name, progress, cloud, speed, cancellationToken, io)
                .ConfigureAwait(false);
            files = journal.GetFiles().Where(f => f.Status != FileCopyStatus.Skipped).ToList();
            pending = files.Where(f => f.Status != FileCopyStatus.Unpacked).ToList();
            zipPending = catcherPack
                ? pending
                : pending.Where(f => !CopyBesideZip(job, f)).ToList();
            loosePending = catcherPack
                ? []
                : pending.Where(f => CopyBesideZip(job, f)).ToList();
            if (zipPending.Count == 0)
            {
                await CopyAlreadyCompressedAsync(
                        job, journal, budget, pause, log, name, progress, cloud, speed, loosePending, cancellationToken, io)
                    .ConfigureAwait(false);
                ZipPack.DeleteTransport(zipPath);
                return;
            }

            if (!File.Exists(zipPath))
            {
                throw new FileNotFoundException("Transport zip is missing after pack; cannot unpack.", zipPath);
            }
        }

        log.Info(job.Id, name,
            $"Unpacking {zipPending.Count} files from {zipPath} directly to {mapping.DestRoot} (zip kept until unpack finishes).");

        var deferred = new DeferredRetrySession(job, journal, log, name);
        var reporter = progress as JobProgressReporter;
        reporter?.Update(speed: speed);
        var byRel = zipPending.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        var attempts = Math.Max(1, job.Options.RetryCount + 1);
        Exception? last = null;
        for (var i = 0; i < attempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ZipPack.ExtractAsync(
                    zipPath,
                    mapping.DestRoot,
                    zipPending,
                    job.Options.Overwrite,
                    job,
                    budget,
                    pause,
                    speed,
                    log,
                    name,
                    progress,
                    cloud,
                    WaitForHoursAsync,
                    relative =>
                    {
                        journal.MarkUnpacked(relative);
                        deferred.Remove(relative);
                    },
                    cancellationToken,
                    onTransientSkip: (relative, ex) =>
                    {
                        if (!DeferredRetry.IsTransient(ex) || !byRel.TryGetValue(relative, out var file))
                        {
                            return false;
                        }

                        deferred.Defer(file, ex.Message);
                        return true;
                    },
                    onAfterFile: async rel =>
                    {
                        if (reporter is not null)
                        {
                            await PauseAfterFileIfRequestedAsync(job, journal, pause, log, name, rel, reporter, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }).ConfigureAwait(false);

                if (deferred.HasItems)
                {
                    log.Info(job.Id, name, $"Retrying deferred files (end of pass, {deferred.Items.Count} file(s)).");
                    reporter?.Update("Retrying deferred files");
                    var retry = deferred.Items.ToList();
                    await ZipPack.ExtractAsync(
                        zipPath,
                        mapping.DestRoot,
                        retry,
                        job.Options.Overwrite,
                        job,
                        budget,
                        pause,
                        speed,
                        log,
                        name,
                        progress,
                        cloud,
                        WaitForHoursAsync,
                        relative =>
                        {
                            journal.MarkUnpacked(relative);
                            deferred.Remove(relative);
                        },
                        cancellationToken,
                        onTransientSkip: (relative, ex) =>
                        {
                            if (!DeferredRetry.IsTransient(ex) || !byRel.TryGetValue(relative, out var file))
                            {
                                return false;
                            }

                            deferred.Keep(file, ex.Message);
                            return true;
                        }).ConfigureAwait(false);
                }

                deferred.FinalizeFailures();
                await CopyAlreadyCompressedAsync(
                        job, journal, budget, pause, log, name, progress, cloud, speed, loosePending, cancellationToken, io)
                    .ConfigureAwait(false);
                var leftover = journal.GetFiles()
                    .Where(f => f.Status is not FileCopyStatus.Skipped and not FileCopyStatus.Unpacked and not FileCopyStatus.Failed)
                    .ToList();
                if (leftover.Count > 0)
                {
                    throw new IOException($"{leftover.Count} file(s) were not unpacked.");
                }

                var failed = journal.GetFiles(FileCopyStatus.Failed);
                if (failed.Count == 0)
                {
                    ZipPack.DeleteTransport(zipPath);
                    log.Info(job.Id, name, $"Unpacked {zipPending.Count} files to {mapping.DestRoot}; removed transport zip.");
                }
                else
                {
                    log.Info(job.Id, name,
                        $"Unpack finished with {failed.Count} issue(s). Transport zip kept at {zipPath} for Resume last.");
                }

                return;
            }
            catch (OperationCanceledException)
            {
                log.Info(job.Id, name,
                    $"Stop during unpack — transport zip kept at {zipPath}. Resume last continues unpack.");
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                log.Error(job.Id, name, $"Unpack retry {i + 1}/{attempts}: {ex.Message}");
                if (i + 1 < attempts)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, job.Options.RetryWaitSeconds)), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new IOException(last?.Message ?? "Unpack failed after retries", last);
    }

    private static bool ShouldSkipZip(string zipPath, IReadOnlyList<FileRecord> files, JobOptions options)
    {
        if (!File.Exists(zipPath) || files.Count == 0)
        {
            return false;
        }

        if (options.Overwrite == OverwritePolicy.Always)
        {
            return false;
        }

        if (options.Overwrite == OverwritePolicy.NeverIfExists)
        {
            return true;
        }

        var zipTime = File.GetLastWriteTimeUtc(zipPath);
        var newest = files.Max(f => f.LastWriteUtc);
        return zipTime + FileMetadata.ComparisonTolerance(options) >= newest;
    }

    public static async Task<string?> CopyOneAsync(
        Job job,
        FileRecord file,
        BandwidthBudget budget,
        PauseGate? pause,
        SpeedTracker? speed,
        CancellationToken cancellationToken,
        IJobLog? log = null,
        string? jobName = null,
        UnbufferedIoSession? io = null,
        Action<long>? onCopied = null)
    {
        return await FileCopier.CopyAsync(job, file, budget, pause, speed, cancellationToken, log, jobName, io, onCopied)
            .ConfigureAwait(false);
    }

    private static void LogIoPlan(Job job, CopyMapping mapping, UnbufferedIoSession io, IJobLog log, string name)
    {
        log.Info(job.Id, name, io.StartupLine());
        if (VolumeInfo.IsRemovableOrNetwork(job.SourcePath) ||
            (job.Catcher is null && VolumeInfo.IsRemovableOrNetwork(mapping.DestRoot)))
        {
            log.Info(job.Id, name,
                "Source or destination looks like USB or network — unbuffered I/O often helps for large sequential files.");
        }
    }

    private static async Task PulseDirtyAsync(
        Job job,
        JobJournal journal,
        PauseGate pause,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var totals = journal.Totals();
                    var percent = totals.Bytes > 0 ? 100.0 * totals.DoneBytes / totals.Bytes : 0;
                    JobHeartbeat.Write(journal, job.Id, percent, pause.CurrentFilePath);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // never fail the copy because heartbeat could not flush
                }
            }
        }
        catch (OperationCanceledException)
        {
            // run finished
        }
    }

    private static bool TryJournal(JobJournal journal, Action action, IJobLog log, string jobId, string name)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (JobJournal.IsJournalFault(ex))
        {
            log.Error(jobId, name, "Journal unavailable: " + ProgressHeader.DescribeFileError(ex));
            return false;
        }
    }

    private static void TryDeleteIncomplete(string path)
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

    private static async Task WaitForHoursAsync(
        Job job,
        PauseGate pause,
        IJobLog log,
        string name,
        IProgress<JobProgress>? progress,
        bool cloud,
        CancellationToken cancellationToken)
    {
        if (!job.Options.HoursEnabled)
        {
            return;
        }

        var logged = false;
        while (!RunWindow.IsInside(TimeOnly.FromDateTime(DateTime.Now), job.Options.HoursStart, job.Options.HoursEnd))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            if (!logged)
            {
                logged = true;
                var previous = job.Status;
                job.Status = JobStatus.PausedOutsideHours;
                log.Info(job.Id, name, $"Paused — outside hours ({job.Options.HoursStart:HH:mm}–{job.Options.HoursEnd:HH:mm}).");
                Report(progress, job, name, cloud, "Paused — outside hours");
                job.Status = previous;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        if (logged)
        {
            job.Status = JobStatus.Copying;
            log.Info(job.Id, name, "Inside hours — resuming copy.");
        }
    }

    private static void Report(
        IProgress<JobProgress>? progress,
        Job job,
        string name,
        bool cloud,
        string? message,
        string? current = null,
        SpeedTracker? speed = null)
    {
        if (progress is null)
        {
            return;
        }

        progress.Report(new JobProgress
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

public sealed class SpeedTracker
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _bytes;
    private long _windowBytes;
    private long _windowStart = Stopwatch.GetTimestamp();

    public long Bytes => Interlocked.Read(ref _bytes);
    public double BytesPerSecond { get; private set; }

    public double EffectiveBytesPerSecond
    {
        get
        {
            var instant = BytesPerSecond;
            if (instant >= 1)
            {
                return instant;
            }

            return ByteFormatter.EffectiveRate(0, Bytes, _clock.Elapsed);
        }
    }

    public void Add(long count)
    {
        Interlocked.Add(ref _bytes, count);
        Interlocked.Add(ref _windowBytes, count);
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (elapsed >= 0.5)
        {
            BytesPerSecond = _windowBytes / elapsed;
            _windowBytes = 0;
            _windowStart = now;
        }
        else if (_clock.Elapsed.TotalSeconds > 0)
        {
            BytesPerSecond = Bytes / _clock.Elapsed.TotalSeconds;
        }
    }
}
