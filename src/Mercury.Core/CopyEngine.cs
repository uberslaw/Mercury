using System.Diagnostics;
using System.IO.Compression;
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
        var destRoots = JobDestinations.Roots(job);
        var cloud = catcher is null && destRoots.Any(CloudPath.LooksLikeCloudFolder);
        if (cloud)
        {
            log.Info(job.Id, name, destRoots.Count > 1
                ? "A destination looks like a cloud-synced folder. Mercury verifies the local copy, not the cloud upload."
                : "Destination looks like a cloud-synced folder. Mercury verifies the local copy, not the cloud upload.");
        }

        if (catcher is not null)
        {
            log.Info(job.Id, name,
                $"Catcher destination {CatcherCrypto.FormatBaseUrl(catcher.PublicHost, catcher.PublicPort)}. TLS fingerprint pin is required. HTTPS zip-pack push — per-file resume is not in v1.");
        }

        var destForShape = catcher is null ? job.DestinationPath : journal.Directory;
        var mappings = JobSources.Resolve(job, destForShape, job.Options.IncludeSourceFolderName).ToList();
        if (mappings.Count == 0)
        {
            throw new DirectoryNotFoundException("No source folders on this job.");
        }

        if (catcher is not null && mappings.Count > 1)
        {
            var combinedZip = Path.Combine(journal.Directory, "mercury-send.zip");
            mappings = mappings.Select(m => new CopyMapping
            {
                Kind = m.Kind,
                SourceRoot = m.SourceRoot,
                DestRoot = m.DestRoot,
                SingleFile = m.SingleFile,
                SingleFileName = m.SingleFileName,
                UniqueRelativePrefix = m.UniqueRelativePrefix,
                TransportZipPath = combinedZip,
                UserDestPath = m.UserDestPath
            }).ToList();
        }

        var mapping = PackDestination(job, mappings, journal.Directory);
        job.SourceKind = mappings.Count == 1 ? mappings[0].Kind : SourceKind.Folder;
        job.VolumeSerial ??= VolumeInfo.GetSerial(job.SourcePath);
        JobDestinations.EnsureList(job);

        if (destRoots.Count > 1)
        {
            log.Info(job.Id, name,
                $"Copying to {destRoots.Count} destinations in this job (one after another). Pack-as-zip, if on, packs once per destination.");
        }

        if (job.StartedUtc is null)
        {
            TransferRundown.MarkStarted(job);
        }

        if (catcher is not null && !mapping.SingleFile)
        {
            job.Options.PackAsZip = true;
        }

        var pack = catcher is not null ? mappings.All(m => !m.SingleFile) : mappings.Any(m => ZipPack.Applies(job, m));
        var totals = journal.Totals();
        var hasJournal = totals.Files > 0;
        if (hasJournal)
        {
            CopyShape.BindJournalToLanding(journal, mappings);
        }
        var stages = CopyPipeline.For(job, hasJournal, pack);
        var reporter = new JobProgressReporter(job, name, cloud, stages, progress, log, pause);

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = reporter.HeartbeatAsync(heartbeatCts.Token);
        var dirtyPulse = PulseDirtyAsync(job, journal, pause, heartbeatCts.Token);
        UnbufferedIoSession? io = null;
        try
        {
            reporter.Enter(CopyStageKind.PreparingDestination, JobStatus.Preparing,
                catcher is null
                    ? destRoots.Count > 1
                        ? $"Preparing {destRoots.Count} destinations…"
                        : "Preparing destination…"
                    : "Preparing Catcher send…");
            if (destRoots.Count > 1)
            {
                reporter.SetDestination(1, destRoots.Count, destRoots[0]);
            }

            journal.SaveJob(job);
            var preflight = await PreflightAsync(job, mappings, log, name, cancellationToken).ConfigureAwait(false);
            foreach (var failed in preflight.Failed)
            {
                journal.AddIssue(new TransferIssue
                {
                    RelativePath = failed.Dest,
                    Kind = IssueKind.CopyError,
                    Message = failed.Error
                });
                log.Error(job.Id, name, $"Destination failed, continuing with the rest: {failed.Dest}. {failed.Error}");
            }

            if (preflight.Mappings.Count == 0)
            {
                throw new IOException(preflight.Failed.Count > 0
                    ? preflight.Failed[0].Error
                    : "No writable destinations.");
            }

            mappings = preflight.Mappings;
            mapping = PackDestination(job, mappings, journal.Directory);

            if (!hasJournal)
            {
                reporter.Enter(CopyStageKind.EnumeratingSource, JobStatus.Enumerating, "Enumerating source…");
                log.Info(job.Id, name, "Enumerating source files.");
                var exclude = new List<string>();
                if (pack)
                {
                    foreach (var map in mappings)
                    {
                        exclude.AddRange(ZipPack.ExcludePaths(map));
                    }
                }

                var found = 0;
                var foundBytes = 0L;
                var lastPulse = Stopwatch.GetTimestamp();
                var inventory = new PayloadInventory();
                var peek = new MagicPeekBudget();
                var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var uniquePrefix = mappings.Count > 1;
                var batch = new List<FileRecord>();
                foreach (var record in SourceWalker.WalkAll(mappings, exclude, job.Options, uniquePrefix))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var kind = FileClassifier.Classify(record.SourcePath, record.Size, peek);
                    record.PayloadKind = kind;
                    inventory.Add(kind, record.Size);
                    ZipPack.NoteFolders(folders, record.RelativePath);
                    batch.Add(record);
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

                cancellationToken.ThrowIfCancellationRequested();
                var nameNotes = DestNameAdjuster.Resolve(batch, job.Options.FixLongOrDuplicateNames);
                foreach (var record in batch)
                {
                    journal.UpsertFile(record);
                }

                DestNameAdjuster.Publish(job, journal, log, name, nameNotes);

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
                var enumerateMessage = nameNotes.Count == 0
                    ? $"Enumerating source… {totals.Files} files"
                    : $"Enumerating source… {totals.Files} files. {nameNotes.Count} long or duplicate name(s).";
                reporter.Update(
                    enumerateMessage,
                    filesTotal: totals.Files,
                    bytesTotal: totals.Bytes,
                    typeSummary: typeSummary);

                var ioFromWalk = UnbufferedIoSession.Create(job.Options, inventory, budget);
                LogIoPlan(job, mapping, ioFromWalk, log, name);
                io = ioFromWalk;

                if (catcher is null)
                {
                    reporter.Enter(CopyStageKind.CheckingDestinationSpace, JobStatus.Enumerating, "Checking destination space…");
                    CheckSpacePerDestination(job, mappings, journal.GetFiles(), pack, journal, log, name);
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
                        foreach (var map in mappings)
                        {
                            exclude.AddRange(ZipPack.ExcludePaths(map));
                        }
                    }

                    var resumeNotes = new List<DestNameAdjuster.Note>();
                    var scan = JournalReconcile.Scan(
                        mappings,
                        journal,
                        job.Options,
                        exclude,
                        (seen, file) => reporter.Update(
                            $"Checking source for changes… {seen} files",
                            file,
                            filesTotal: Math.Max(seen, totals.Files),
                            bytesTotal: totals.Bytes),
                        cancellationToken,
                        uniquePrefix: mappings.Count > 1,
                        nameNotes: resumeNotes);
                    DestNameAdjuster.Publish(job, journal, log, name, resumeNotes);
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

            var listed = journal.GetFiles();
            var plan = TransferPlanner.Build(job, listed, mappings);
            if (pack && catcher is null)
            {
                if (!plan.HasPacking && !NeedsTransportZip(job, listed))
                {
                    pack = false;
                    log.Info(job.Id, name,
                        $"All {listed.Count} file(s) are already compressed (video, photos, audio, archives, disk images) — copying as-is, no transport zip.");
                }
            }

            foreach (var pocket in plan.PackGroups.SelectMany(g => g.Pockets).Where(p => p.Disposition == PackDisposition.Sample).ToList())
            {
                var sampleSize = PackSample.ClampSampleBytes(TransferPlanner.SampleBytes, pocket.TotalBytes);
                log.Info(job.Id, name,
                    $"Sampling pocket {pocket.Id} ({ByteFormatter.ToString(Math.Min(sampleSize, pocket.TotalBytes))}) to see if packing helps.");
                var sample = PackSample.Evaluate(pocket.Files, sampleSize);
                log.Info(job.Id, name, sample.FormatLine());
                TransferPlanner.ApplySample(plan, pocket, sample);
            }

            pack = catcher is not null ? mappings.All(m => !m.SingleFile) : plan.HasPacking;
            var overlap = pack && catcher is null && plan.Stream.Count > 0 && plan.HasPacking;
            reporter.ReplaceStages(CopyPipeline.For(job, hasJournalFiles: true, pack, overlap));
            if (!string.IsNullOrWhiteSpace(plan.Summary))
            {
                log.Info(job.Id, name, plan.Summary);
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
                foreach (var map in mappings)
                {
                    var destDir = map.SingleFile
                        ? Path.GetDirectoryName(CopyShape.LandingPath(map))
                        : map.DestRoot;
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }
                }
            }

            var speed = new SpeedTracker();
            var transferBytesBefore = Math.Max(0, job.TransferBytes);
            var transferSecondsBefore = Math.Max(0, job.TransferSeconds);
            try
            {
                if (pack && overlap)
                {
                    reporter.Enter(CopyStageKind.Transferring, JobStatus.Copying, "Copying and packing…");
                    journal.SaveJob(job);
                    await RunOverlappedAsync(
                            job, plan, journal, budget, pause, log, name, reporter, cloud, speed, cancellationToken, io)
                        .ConfigureAwait(false);
                }
                else if (pack)
                {
                    reporter.Enter(CopyStageKind.Transferring, JobStatus.Copying, "Packing…");
                    journal.SaveJob(job);
                    foreach (var group in PackTargets(job, mapping, plan, catcher is not null))
                    {
                        await PackWithRetriesAsync(
                                job, group.Mapping, journal, budget, pause, log, name, reporter, cloud, speed, cancellationToken, io,
                                onlyThese: catcher is not null ? null : group.Files,
                                copyLoose: catcher is null && plan.Stream.Count > 0 && !overlap,
                                compression: group.Compression)
                            .ConfigureAwait(false);
                    }

                    if (catcher is null)
                    {
                        var anyZip = PackTargets(job, mapping, plan, false)
                            .Any(g => File.Exists(ZipPack.ZipPath(g.Mapping)));
                        if (anyZip)
                        {
                            reporter.Enter(CopyStageKind.Unpacking, JobStatus.Copying, "Unpacking…");
                            journal.SaveJob(job);
                            foreach (var group in PackTargets(job, mapping, plan, false))
                            {
                                if (!File.Exists(ZipPack.ZipPath(group.Mapping)))
                                {
                                    continue;
                                }

                                await UnpackWithRetriesAsync(
                                        job, group.Mapping, journal, budget, pause, log, name, reporter, cloud, speed, cancellationToken, io)
                                    .ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            log.Info(job.Id, name, "No transport zip to unpack — already-compressed files were copied as-is.");
                        }
                    }
                }
                else if (catcher is null)
                {
                    reporter.Enter(CopyStageKind.Transferring, JobStatus.Copying, "Copying…");
                    journal.SaveJob(job);
                    var pending = journal.GetFiles()
                        .Where(f => f.Status is FileCopyStatus.Pending or FileCopyStatus.Failed or FileCopyStatus.Deferred)
                        .ToList();
                    await CopyPendingListAsync(
                            job, journal, budget, pause, log, name, reporter, cloud, speed, pending, totals, cancellationToken, io,
                            markUnpacked: false)
                        .ConfigureAwait(false);
                }

                if (catcher is null)
                {
                    foreach (var map in mappings)
                    {
                        FileMetadata.FinishDestination(job, map, journal, log, name);
                    }
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

                journal.SaveJob(job);
            }
            finally
            {
                speed.ApplyTo(job, transferBytesBefore, transferSecondsBefore);
            }
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

    public void Verify(
        Job job,
        JobJournal journal,
        IJobLog log,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        job.Options ??= new JobOptions();
        if (job.Options.DryRun
            || job.Status is JobStatus.Failed or JobStatus.Cancelled)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(job.Name) ? job.Id[..8] : job.Name;
        var catcher = job.Catcher;
        var destRoots = JobDestinations.Roots(job);
        var cloud = catcher is null && destRoots.Any(CloudPath.LooksLikeCloudFolder);
        var destForShape = catcher is null ? job.DestinationPath : journal.Directory;
        IReadOnlyList<CopyMapping> mappings;
        try
        {
            mappings = JobSources.Resolve(job, destForShape, job.Options.IncludeSourceFolderName).ToList();
        }
        catch (Exception ex)
        {
            log.Error(job.Id, name, "Verify could not resolve landing paths: " + ex.Message);
            job.Status = JobStatus.Incomplete;
            job.ResultMessage = "Incomplete — could not resolve destination for verify.";
            return;
        }

        if (mappings.Count == 0)
        {
            job.Status = JobStatus.Incomplete;
            job.ResultMessage = "Incomplete — no source folders to verify.";
            return;
        }

        CopyShape.BindJournalToLanding(journal, mappings);
        var mapping = PackDestination(job, mappings, journal.Directory);
        var pack = catcher is not null ? mappings.All(m => !m.SingleFile) : mappings.Any(m => ZipPack.Applies(job, m));
        var totals = journal.Totals();
        var stages = CopyPipeline.For(job, hasJournalFiles: true, pack);
        var reporter = new JobProgressReporter(job, name, cloud, stages, progress, log);
        reporter.Enter(CopyStageKind.Verifying, JobStatus.Verifying, "Verifying…");
        journal.SaveJob(job);
        log.Info(job.Id, name, "Verify running in background");
        log.Info(job.Id, name, $"Verify started ({job.Options.Verify}).");

        int issues;
        try
        {
            issues = catcher is not null && pack
                ? ZipPack.Verify(job, journal, mapping, log, name)
                : catcher is not null && !pack
                    ? 0
                    : Verifier.Verify(
                        job,
                        journal,
                        mapping,
                        log,
                        name,
                        cancellationToken,
                        pulse =>
                        {
                            reporter.Update(
                                current: pulse.Phase,
                                filesCopied: pulse.Done,
                                filesTotal: pulse.Total,
                                bytesCopied: totals.DoneBytes > 0 ? totals.DoneBytes : job.BytesCopied,
                                bytesTotal: totals.DoneBytes > 0 ? totals.DoneBytes : job.BytesCopied);
                            progress?.Report(new JobProgress
                            {
                                JobId = job.Id,
                                JobName = name,
                                Status = JobStatus.Verifying,
                                Message = destRoots.Count > 1
                                    ? $"Verifying… destination {Math.Max(1, JobDestinations.IndexOf(pulse.Phase, destRoots))} of {destRoots.Count}"
                                    : "Verifying…",
                                CurrentFile = CopyShape.ProgressRelative(job, pulse.Phase),
                                CloudDestination = cloud,
                                BytesCopied = totals.DoneBytes > 0 ? totals.DoneBytes : job.BytesCopied,
                                BytesTotal = totals.DoneBytes > 0 ? totals.DoneBytes : job.BytesCopied,
                                FilesCopied = pulse.Done,
                                FilesTotal = pulse.Total,
                                StageIndex = CopyPipeline.IndexOf(stages, CopyStageKind.Verifying),
                                StageCount = stages.Count,
                                StageName = "Verifying",
                                DestinationIndex = destRoots.Count > 1 ? JobDestinations.IndexOf(pulse.Phase, destRoots) : 0,
                                DestinationCount = destRoots.Count > 1 ? destRoots.Count : 0,
                                CurrentDestination = destRoots.Count > 1
                                    ? destRoots[Math.Clamp(JobDestinations.IndexOf(pulse.Phase, destRoots) - 1, 0, destRoots.Count - 1)]
                                    : null,
                                StartedUtc = job.StartedUtc,
                                StageStartedUtc = DateTimeOffset.UtcNow,
                                Eta = pulse.Eta,
                                RundownDone = pulse.Done,
                                RundownTotal = pulse.Total,
                                RundownPerSecond = pulse.UnitsPerSecond
                            });
                        },
                        mappings: mappings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (destRoots.Count > 1)
        {
            issues = Math.Max(issues, journal.IssueCount());
        }

        job.IssueCount = issues;
        string complete;
        if (catcher is not null)
        {
            complete = pack
                ? $"Verified complete. Catcher unpacked the transfer at {CatcherCrypto.FormatBaseUrl(catcher.PublicHost, catcher.PublicPort)}."
                : $"Verified complete. Sent file to Catcher {CatcherCrypto.FormatBaseUrl(catcher.PublicHost, catcher.PublicPort)}.";
        }
        else if (pack)
        {
            complete = $"Verified complete. Unpacked {totals.Files} files to {mapping.DestRoot}.";
        }
        else if (cloud)
        {
            complete = "Verified complete (local destination). Cloud upload is the sync client’s job.";
        }
        else
        {
            complete = "Verified complete.";
        }

        var incomplete = $"Incomplete — {issues} issue(s). Open the log for details.";
        job.ResultMessage = JobDestinations.VerifyMessage(job, journal, issues, complete, incomplete);
        job.Status = issues == 0 ? JobStatus.Completed : JobStatus.Incomplete;
        if (issues == 0)
        {
            log.Info(job.Id, name, job.ResultMessage);
        }
        else
        {
            log.Error(job.Id, name, job.ResultMessage);
        }

        reporter.Update(job.ResultMessage);
        journal.SaveJob(job);
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
        var length = new FileInfo(payloadPath).Length;
        log.Info(job.Id, name, $"Sending {fileName} ({ByteFormatter.ToString(length)}) over HTTPS.");
        pause.BeginFile(fileName, length);
        reporter.Update($"Sending {fileName} over HTTPS…", fileName, speed);
        CatcherPushResult result;
        try
        {
            result = await CatcherClient.PushAsync(
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
                    pause.AddFileBytes(bytes);
                },
                cancellationToken,
                unpack: pack).ConfigureAwait(false);
        }
        finally
        {
            pause.EndFile();
        }

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

    private static void PulseDestination(Job job, JobProgressReporter reporter, FileRecord file)
    {
        var dests = JobDestinations.Roots(job);
        if (dests.Count <= 1)
        {
            return;
        }

        var index = JobDestinations.IndexOf(file.DestPath, dests);
        reporter.SetDestination(index, dests.Count, dests[Math.Clamp(index - 1, 0, dests.Count - 1)]);
    }

    private readonly record struct DestFailure(string Dest, string Error);

    private readonly record struct PreflightResult(List<CopyMapping> Mappings, List<DestFailure> Failed);

    private static async Task<PreflightResult> PreflightAsync(
        Job job,
        IReadOnlyList<CopyMapping> mappings,
        IJobLog log,
        string name,
        CancellationToken cancellationToken)
    {
        foreach (var source in JobSources.Roots(job))
        {
            var timeout = PathProbe.TimeoutFor(source);
            if (!await PathProbe.ExistsAsync(source, timeout, cancellationToken).ConfigureAwait(false))
            {
                throw new DirectoryNotFoundException($"Source not found: {source}");
            }
        }

        var groups = mappings
            .GroupBy(m => JobDestinations.UserDest(m), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var kept = new List<CopyMapping>();
        var failed = new List<DestFailure>();
        foreach (var group in groups)
        {
            var probeMapping = group.First();
            try
            {
                await ProbeDestinationAsync(job, probeMapping, log, name, cancellationToken).ConfigureAwait(false);
                kept.AddRange(group);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var dest = group.Key;
                var error = $"Destination is not writable: {dest}. {ex.Message}";
                if (groups.Count == 1)
                {
                    throw new IOException(error, ex);
                }

                failed.Add(new DestFailure(dest, error));
            }
        }

        return new PreflightResult(kept, failed);
    }

    private static async Task ProbeDestinationAsync(
        Job job,
        CopyMapping mapping,
        IJobLog log,
        string name,
        CancellationToken cancellationToken)
    {
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
                log.Info(job.Id, name, $"Destination free space ({JobDestinations.UserDest(mapping)}): {ByteFormatter.ToString(available.Value)}.");
            }

            FreeSpace.CheckOrWarn(job, needed, available, log, name);
        }
    }

    private static void CheckSpacePerDestination(
        Job job,
        IReadOnlyList<CopyMapping> mappings,
        IReadOnlyList<FileRecord> files,
        bool pack,
        JobJournal journal,
        IJobLog log,
        string name)
    {
        if (pack)
        {
            log.Info(job.Id, name,
                "Pack as zip writes a transport zip then unpacks at dest (both exist until unpack finishes).");
        }

        foreach (var group in mappings.GroupBy(m => JobDestinations.UserDest(m), StringComparer.OrdinalIgnoreCase))
        {
            var destFiles = files.Where(f => JobDestinations.BelongsTo(f.DestPath, group.Key)).ToList();
            var needed = destFiles.Sum(f => f.Size);
            if (pack)
            {
                needed *= 2;
            }

            var available = FreeSpace.GetAvailableBytes(group.First().DestRoot);
            if (available is not null)
            {
                log.Info(job.Id, name, $"Destination free space ({group.Key}): {ByteFormatter.ToString(available.Value)}.");
            }

            try
            {
                FreeSpace.CheckOrWarn(job, needed, available, log, name);
            }
            catch (IOException ex) when (mappings.Select(JobDestinations.UserDest).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            {
                log.Error(job.Id, name, $"Not enough space at {group.Key}; other destinations continue. {ex.Message}");
                journal.AddIssue(new TransferIssue
                {
                    RelativePath = group.Key,
                    Kind = IssueKind.CopyError,
                    Message = ex.Message
                });
                foreach (var file in destFiles)
                {
                    journal.MarkFailed(file.RelativePath, ex.Message, job.Options.RetryCount);
                }
            }
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

        return PackPolicy.ShouldSkipPacking(file, job.Options, peek);
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

    private static CopyMapping PackDestination(Job job, IReadOnlyList<CopyMapping> mappings, string journalDir)
    {
        if (job.Catcher is not null && mappings.Count > 1)
        {
            return new CopyMapping
            {
                Kind = SourceKind.Folder,
                SourceRoot = mappings[0].SourceRoot,
                DestRoot = journalDir,
                TransportZipPath = Path.Combine(journalDir, "mercury-send.zip")
            };
        }

        return mappings[0];
    }

    private static IReadOnlyList<PackGroup> PackTargets(Job job, CopyMapping packMapping, TransferPlan plan, bool catcher)
    {
        if (catcher)
        {
            return [new PackGroup { Mapping = packMapping }];
        }

        if (plan.PackGroups.Count > 0)
        {
            return plan.PackGroups;
        }

        return [new PackGroup { Mapping = packMapping }];
    }

    private static async Task RunOverlappedAsync(
        Job job,
        TransferPlan plan,
        JobJournal journal,
        BandwidthBudget budget,
        PauseGate pause,
        IJobLog log,
        string name,
        JobProgressReporter reporter,
        bool cloud,
        SpeedTracker speed,
        CancellationToken cancellationToken,
        UnbufferedIoSession? io)
    {
        var totals = journal.Totals();
        var stream = plan.Stream
            .Where(f => f.Status is FileCopyStatus.Pending or FileCopyStatus.Failed or FileCopyStatus.Deferred)
            .ToList();
        var largest = stream.Count == 0 ? 0 : stream.Max(f => f.Size);
        speed.BeginStage();
        reporter.Update(speed: speed);
        var adaptive = await AdaptiveCopyPlanner.PrepareAsync(
                job, budget, stream, plan.HasPacking, largest, log, name,
                (label, file) => reporter.ShowStatus(label, file),
                cancellationToken)
            .ConfigureAwait(false);
        speed.BeginStage();
        if (adaptive.UseBalancer)
        {
            await CopyPendingListAsync(
                    job, journal, budget, pause, log, name, reporter, cloud, speed, stream, totals, cancellationToken, io,
                    markUnpacked: true, adaptive: adaptive, overlapPlan: plan, adaptiveResolved: true)
                .ConfigureAwait(false);
        }
        else
        {
            var packTask = PackGroupsAsync(
                job, plan, journal, budget, pause, log, name, reporter, cloud, speed, cancellationToken, io);
            try
            {
                await CopyPendingListAsync(
                        job, journal, budget, pause, log, name, reporter, cloud, speed, stream, totals, cancellationToken, io,
                        markUnpacked: true, adaptive: adaptive, adaptiveResolved: true)
                    .ConfigureAwait(false);
                await packTask.ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await packTask.ConfigureAwait(false);
                }
                catch
                {
                    // primary exception below
                }

                throw;
            }
        }

        reporter.Enter(CopyStageKind.Unpacking, JobStatus.Copying, "Unpacking…");
        journal.SaveJob(job);
        foreach (var group in plan.PackGroups)
        {
            if (!File.Exists(ZipPack.ZipPath(group.Mapping)))
            {
                continue;
            }

            await UnpackWithRetriesAsync(
                    job, group.Mapping, journal, budget, pause, log, name, reporter, cloud, speed, cancellationToken, io)
                .ConfigureAwait(false);
        }
    }

    private static async Task PackGroupsAsync(
        Job job,
        TransferPlan plan,
        JobJournal journal,
        BandwidthBudget budget,
        PauseGate pause,
        IJobLog log,
        string name,
        JobProgressReporter reporter,
        bool cloud,
        SpeedTracker speed,
        CancellationToken cancellationToken,
        UnbufferedIoSession? io)
    {
        foreach (var group in plan.PackGroups)
        {
            await PackWithRetriesAsync(
                    job, group.Mapping, journal, budget, pause, log, name, reporter, cloud, speed, cancellationToken, io,
                    onlyThese: group.Files,
                    copyLoose: false,
                    compression: group.Compression)
                .ConfigureAwait(false);
        }
    }

    private readonly record struct CopyWork(
        bool Ok,
        bool Finished,
        string? Hash,
        string? Error,
        bool Transient,
        long BytesOnDisk);

    private static async Task CopyBalancedAsync(
        Job job,
        JobJournal journal,
        BandwidthBudget budget,
        PauseGate pause,
        IJobLog log,
        string name,
        JobProgressReporter reporter,
        bool cloud,
        SpeedTracker speed,
        List<FileRecord> pending,
        FileTotals totals,
        CancellationToken cancellationToken,
        UnbufferedIoSession? io,
        bool markUnpacked,
        AdaptivePlan adaptive,
        TransferPlan? overlapPlan)
    {
        var deferred = new DeferredRetrySession(job, journal, log, name);
        var queue = new LinkedList<FileRecord>();
        foreach (var file in pending)
        {
            PulseDestination(job, reporter, file);
            cancellationToken.ThrowIfCancellationRequested();
            await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            if (ShouldSkip(file, job.Options))
            {
                TryJournal(journal, () => journal.MarkSkipped(file.RelativePath, "Destination is newer or equal"), log, job.Id, name);
                log.Info(job.Id, name, $"Skip {file.RelativePath} (dest newer or equal)");
                continue;
            }

            queue.AddLast(file);
        }

        var fileCount = Math.Max(pending.Count, totals.Files);
        var totalBytes = totals.Bytes;
        var mode = adaptive.Mode;
        var dropped = false;
        var appliedChoice = job.HeaderCopyMode;
        var appliedWidth = job.ManualFileWidth;
        var manual = HeaderCopy.IsManual(appliedChoice);
        string? manualSwitch = manual ? job.HeaderCopySwitchDetail : null;
        var sustain = new AdaptiveSustainTracker();
        var sustainClock = Stopwatch.StartNew();
        var heartbeat = Stopwatch.StartNew();
        sustain.Start(speed.Bytes, 0);
        speed.BeginStage();
        Task? packTask = null;
        var active = new List<(FileRecord File, Task<CopyWork> Task)>();
        string? lastFinished = null;
        var loggedSmall = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task<bool> ApplyHeaderChoiceAsync()
        {
            var choice = job.HeaderCopyMode;
            var widthNow = job.ManualFileWidth;
            if (choice == appliedChoice && (choice != HeaderCopyMode.FilesN || widthNow == appliedWidth))
            {
                return false;
            }

            var from = appliedChoice;
            appliedChoice = choice;
            appliedWidth = choice == HeaderCopyMode.FilesN ? widthNow : 0;
            if (HeaderCopy.UsesAdaptive(choice, job.Options.AdaptiveCopy))
            {
                manual = false;
                manualSwitch = null;
                dropped = false;
                reporter.ShowStatus(HeaderCopy.SwitchLine(from, choice), pause.CurrentFilePath);
                long largestPending = 0;
                foreach (var queued in queue)
                {
                    if (queued.Size > largestPending)
                    {
                        largestPending = queued.Size;
                    }
                }

                var plan = await AdaptiveCopyPlanner.PrepareAsync(
                        job, budget, queue.ToList(), overlapPlan?.HasPacking == true, largestPending, log, name,
                        (label, file) => reporter.ShowStatus(label, file),
                        cancellationToken)
                    .ConfigureAwait(false);
                adaptive = plan;
                mode = plan.Mode;
                sustain.Start(speed.Bytes, sustainClock.Elapsed.TotalSeconds);
                return true;
            }

            manual = true;
            dropped = false;
            mode = HeaderCopy.EngineMode(choice);
            manualSwitch = job.HeaderCopySwitchDetail ?? HeaderCopy.SwitchDetail(from, choice, appliedWidth);
            reporter.ShowStatus(HeaderCopy.RunningStatus(mode, manualSwitch, fileWidth: appliedWidth), pause.CurrentFilePath);
            return true;
        }

        try
        {
        while (queue.Count > 0 || active.Count > 0 || packTask is { IsCompleted: false })
        {
            cancellationToken.ThrowIfCancellationRequested();
            await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            await WaitForHoursAsync(job, pause, log, name, reporter, cloud, cancellationToken).ConfigureAwait(false);

            for (var i = active.Count - 1; i >= 0; i--)
            {
                if (!active[i].Task.IsCompleted)
                {
                    continue;
                }

                var slot = active[i];
                active.RemoveAt(i);
                var work = await slot.Task.ConfigureAwait(false);
                if (work.Ok && !work.Finished)
                {
                    var madeProgress = work.BytesOnDisk > slot.File.BytesCopied;
                    slot.File.BytesCopied = work.BytesOnDisk;
                    if (!madeProgress || pause.PauseAfterFileRequested)
                    {
                        pause.BeginFile(slot.File.RelativePath, slot.File.Size);
                        active.Add((slot.File, CopyAdaptiveFileAsync(
                            job, slot.File, journal, budget, pause, log, name, speed, cancellationToken, io, stripe: false, maxNewBytes: null)));
                    }
                    else
                    {
                        queue.AddFirst(slot.File);
                    }

                    continue;
                }

                RecordCopyOutcome(job, journal, deferred, log, name, slot.File, work, markUnpacked);
                if (work.Ok && work.Finished)
                {
                    lastFinished = slot.File.RelativePath;
                }

                try
                {
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

            if (pause.PauseAfterFileRequested && active.Count == 0)
            {
                await PauseAfterFileIfRequestedAsync(
                        job, journal, pause, log, name, lastFinished ?? "", reporter, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (await ApplyHeaderChoiceAsync().ConfigureAwait(false))
            {
                manualSwitch = manual ? job.HeaderCopySwitchDetail : null;
            }

            if (!manual && !dropped && mode != AdaptiveCopyMode.Sequential)
            {
                var observed = sustain.Observe(speed.Bytes, sustainClock.Elapsed.TotalSeconds, mode);
                if (observed.LockedBaseline)
                {
                    log.Info(job.Id, name, observed.LogLine ?? "Adaptive copy sustained the chosen mode.");
                    AdaptiveCopyMemory.Remember(job.SourcePath, job.DestinationPath, mode, sustain.BaselineBps);
                }

                if (observed.DropToOneStream)
                {
                    var from = mode;
                    dropped = true;
                    mode = AdaptiveCopyMode.Sequential;
                    AdaptiveCopyMemory.Forget(job.SourcePath, job.DestinationPath);
                    log.Info(job.Id, name, AdaptiveCopyLog.WidthChange(
                        from, mode, observed.LiveBps, observed.ComparisonBps, "parallel copy slowed down"));
                }
            }

            if (pause.PauseAfterFileRequested)
            {
                if (active.Count == 0)
                {
                    if (queue.Count == 0 && packTask is not { IsCompleted: false })
                    {
                        break;
                    }

                    continue;
                }

                await Task.WhenAny(active.Select(slot => slot.Task)).ConfigureAwait(false);
                continue;
            }

            long largest = 0;
            long pendingBytes = 0;
            foreach (var file in queue)
            {
                if (file.Size > largest)
                {
                    largest = file.Size;
                }

                pendingBytes += Math.Max(0, file.Size - file.BytesCopied);
            }

            var step = TransferBalancer.Next(new BalanceInput
            {
                Mode = mode,
                AdaptiveEnabled = true,
                Capped = adaptive.Capped,
                Manual = manual,
                ManualWidth = mode == AdaptiveCopyMode.FilesN ? appliedWidth : 0,
                FellOff = dropped && !manual,
                PendingFiles = queue.Count,
                PendingCopyBytes = pendingBytes,
                LargestPendingBytes = largest,
                PackBytesRemaining = PackBytesRemaining(overlapPlan, packTask),
                PackRunning = packTask is { IsCompleted: false },
                CopyInFlight = active.Count,
                UnverifiedCopied = CountUnverified(journal, job.Id)
            });
            var statusLabel = manual
                ? HeaderCopy.RunningStatus(mode, manualSwitch, fellOff: false, fileWidth: appliedWidth)
                : step.Label;
            if (mode == AdaptiveCopyMode.Ranges2
                && pause.CurrentFilePath is { Length: > 0 } smallPath
                && HeaderCopy.TooSmallToSplit(pause.CurrentFileSize))
            {
                statusLabel = HeaderCopy.TooSmallStatus(smallPath);
            }

            pause.CopyModeLabel = statusLabel;
            reporter.Update(statusLabel, pause.CurrentFilePath, speed);

            if (step.RunPack && packTask is null && overlapPlan is not null)
            {
                packTask = PackGroupsAsync(
                    job, overlapPlan, journal, budget, pause, log, name, reporter, cloud, speed, cancellationToken, io);
            }

            if (step.VerifyBatch > 0 && (active.Count < step.CopyWidth || adaptive.Capped))
            {
                VerifySpare(job, journal, log, name, step.VerifyBatch, cancellationToken);
            }

            var preferLarge = step.PortionBytes is not null || step.Stripe;
            while (active.Count < step.CopyWidth && queue.Count > 0 && !pause.PauseAfterFileRequested)
            {
                var file = TakeQueued(queue, preferLarge);
                var remaining = Math.Max(0, file.Size - file.BytesCopied);
                long? portion = step.PortionBytes is long cap && remaining > cap ? cap : null;
                var tooSmall = mode == AdaptiveCopyMode.Ranges2 && HeaderCopy.TooSmallToSplit(file.Size);
                var stripe = step.Stripe && !tooSmall && file.Size >= AdaptiveCopyPolicy.LargeFileBytes;
                if (tooSmall)
                {
                    if (loggedSmall.Add(file.RelativePath))
                    {
                        log.Info(job.Id, name, HeaderCopy.TooSmallLog(file.RelativePath));
                    }

                    statusLabel = HeaderCopy.TooSmallStatus(file.RelativePath);
                }

                PulseDestination(job, reporter, file);
                pause.BeginFile(file.RelativePath, file.Size);
                active.Add((file, CopyAdaptiveFileAsync(
                    job, file, journal, budget, pause, log, name, speed, cancellationToken, io, stripe, portion)));
            }

            if (active.Count > 0)
            {
                reporter.Update(statusLabel, pause.CurrentFilePath, speed);
            }

            if (heartbeat.Elapsed.TotalSeconds >= AdaptiveCopyPolicy.HeartbeatSeconds)
            {
                heartbeat.Restart();
                log.Info(job.Id, name, AdaptiveCopyLog.Heartbeat(
                    statusLabel, pause.InFlightPaths(), speed.WindowBytesPerSecond, speed.StageBytesPerSecond));
            }

            if (active.Count > 0)
            {
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(TimeSpan.FromSeconds(AdaptiveCopyPolicy.CopyModePollSeconds), wake.Token);
                var finished = await Task.WhenAny(active.Select(slot => slot.Task).Append(delay)).ConfigureAwait(false);
                if (!ReferenceEquals(finished, delay))
                {
                    wake.Cancel();
                }

                try
                {
                    await delay.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // a file finished before the next log line
                }

                continue;
            }

            if (packTask is { IsCompleted: false })
            {
                await Task.WhenAny(packTask, Task.Delay(200, cancellationToken)).ConfigureAwait(false);
                continue;
            }

            break;
        }
        }
        catch
        {
            if (packTask is not null)
            {
                try
                {
                    await packTask.ConfigureAwait(false);
                }
                catch
                {
                    // the copy error is the one to surface
                }
            }

            throw;
        }

        if (packTask is not null)
        {
            await packTask.ConfigureAwait(false);
        }

        await RetryDeferredCopyAsync(
                job, journal, deferred, budget, pause, log, name, reporter, speed, fileCount, totalBytes,
                afterFile: false, endOfPass: true, cancellationToken, io)
            .ConfigureAwait(false);
        deferred.FinalizeFailures();
    }

    private static async Task<CopyWork> CopyAdaptiveFileAsync(
        Job job,
        FileRecord file,
        JobJournal journal,
        BandwidthBudget budget,
        PauseGate pause,
        IJobLog log,
        string name,
        SpeedTracker speed,
        CancellationToken cancellationToken,
        UnbufferedIoSession? io,
        bool stripe,
        long? maxNewBytes)
    {
        var attempts = Math.Max(1, job.Options.RetryCount + 1);
        Exception? last = null;
        try
        {
            for (var i = 0; i < attempts; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await FileCopier.CopyAdaptiveAsync(
                            job, file, budget, pause, speed, cancellationToken, log, name, io,
                            onCopied: n =>
                            {
                                try
                                {
                                    journal.SetCopiedBytes(file.RelativePath, n);
                                }
                                catch (Exception ex) when (JobJournal.IsJournalFault(ex))
                                {
                                    // progress flush must not fail the copy
                                }
                            },
                            stripe: stripe,
                            maxNewBytes: maxNewBytes)
                        .ConfigureAwait(false);
                    if (!result.Finished)
                    {
                        return new CopyWork(true, false, null, null, false, result.BytesOnDisk);
                    }

                    return new CopyWork(true, true, result.Hash, null, false, result.BytesOnDisk);
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

            return new CopyWork(false, true, null, last?.Message ?? "Copy failed", last is not null && DeferredRetry.IsTransient(last), 0);
        }
        finally
        {
            pause.EndFile(file.RelativePath);
        }
    }

    private static void RecordCopyOutcome(
        Job job,
        JobJournal journal,
        DeferredRetrySession deferred,
        IJobLog log,
        string name,
        FileRecord file,
        CopyWork work,
        bool markUnpacked)
    {
        if (!work.Ok)
        {
            var error = work.Error ?? "Copy failed after retries";
            if (work.Transient || ProgressHeader.IsExceptionDump(error))
            {
                deferred.Defer(file, error);
                return;
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
                deferred.Defer(file, error);
            }
            else
            {
                log.Error(job.Id, name, $"Failed {file.RelativePath}: {error}");
            }

            return;
        }

        if (!work.Finished)
        {
            return;
        }

        if (!TryJournal(journal, () =>
            {
                journal.MarkCopied(file.RelativePath, work.Hash);
                if (markUnpacked)
                {
                    journal.MarkUnpacked(file.RelativePath);
                }
            }, log, job.Id, name))
        {
            deferred.Defer(file, "Journal error after copy — deferred for retry");
            return;
        }

        deferred.Remove(file.RelativePath);
        log.Info(job.Id, name, $"Copied {file.RelativePath} ({ByteFormatter.ToString(file.Size)})");
    }

    private static FileRecord TakeQueued(LinkedList<FileRecord> queue, bool preferLarge)
    {
        if (!preferLarge || queue.First is null || queue.Count == 1)
        {
            var first = queue.First!.Value;
            queue.RemoveFirst();
            return first;
        }

        var best = queue.First;
        for (var node = queue.First; node is not null; node = node.Next)
        {
            if (node.Value.Size > best!.Value.Size)
            {
                best = node;
            }
        }

        var file = best!.Value;
        queue.Remove(best);
        return file;
    }

    private static long PackBytesRemaining(TransferPlan? plan, Task? packTask)
    {
        if (plan is null || packTask is { IsCompleted: true })
        {
            return 0;
        }

        long bytes = 0;
        foreach (var group in plan.PackGroups)
        {
            foreach (var file in group.Files)
            {
                if (file.Status is FileCopyStatus.Pending or FileCopyStatus.Failed or FileCopyStatus.Deferred)
                {
                    bytes += Math.Max(0, file.Size - file.BytesCopied);
                }
            }
        }

        return bytes;
    }

    private static int CountUnverified(JobJournal journal, string jobId)
    {
        var count = 0;
        foreach (var file in journal.GetFiles())
        {
            if (file.Status is FileCopyStatus.Copied or FileCopyStatus.Unpacked &&
                !AdaptiveVerifyCache.Contains(jobId, file.RelativePath))
            {
                count++;
            }
        }

        return count;
    }

    private static void VerifySpare(
        Job job,
        JobJournal journal,
        IJobLog log,
        string name,
        int batch,
        CancellationToken cancellationToken)
    {
        var only = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in journal.GetFiles())
        {
            if (only.Count >= batch)
            {
                break;
            }

            if (file.Status is not (FileCopyStatus.Copied or FileCopyStatus.Unpacked))
            {
                continue;
            }

            if (AdaptiveVerifyCache.Contains(job.Id, file.RelativePath))
            {
                continue;
            }

            only.Add(file.RelativePath);
        }

        if (only.Count == 0)
        {
            return;
        }

        var mapping = new CopyMapping
        {
            Kind = job.SourceKind,
            SourceRoot = job.SourcePath,
            DestRoot = job.DestinationPath
        };
        Verifier.Verify(job, journal, mapping, log, name, cancellationToken, onlyThese: only);
        foreach (var relative in only)
        {
            AdaptiveVerifyCache.Mark(job.Id, relative);
        }
    }

    private static async Task CopyPendingListAsync(
        Job job,
        JobJournal journal,
        BandwidthBudget budget,
        PauseGate pause,
        IJobLog log,
        string name,
        JobProgressReporter reporter,
        bool cloud,
        SpeedTracker speed,
        IReadOnlyList<FileRecord> pending,
        FileTotals totals,
        CancellationToken cancellationToken,
        UnbufferedIoSession? io,
        bool markUnpacked,
        AdaptivePlan? adaptive = null,
        TransferPlan? overlapPlan = null,
        bool adaptiveResolved = false)
    {
        var manageCopy = job.Options.AdaptiveCopy
            || HeaderCopy.IsManual(job.HeaderCopyMode)
            || job.HeaderCopyMode == HeaderCopyMode.Adaptive;
        if (!adaptiveResolved && manageCopy)
        {
            var largestPending = pending.Count == 0 ? 0 : pending.Max(f => f.Size);
            speed.BeginStage();
            reporter.Update(speed: speed);
            adaptive = await AdaptiveCopyPlanner.PrepareAsync(
                    job, budget, pending, overlapPlan?.HasPacking == true, largestPending, log, name,
                    (label, file) => reporter.ShowStatus(label, file),
                    cancellationToken)
                .ConfigureAwait(false);
            speed.BeginStage();
        }

        if (adaptive is { } plan && (plan.UseBalancer || job.Options.AdaptiveCopy || HeaderCopy.IsManual(job.HeaderCopyMode) || job.HeaderCopyMode == HeaderCopyMode.Adaptive))
        {
            await CopyBalancedAsync(
                    job, journal, budget, pause, log, name, reporter, cloud, speed, pending.ToList(), totals,
                    cancellationToken, io, markUnpacked, plan, overlapPlan)
                .ConfigureAwait(false);
            return;
        }

        var deferred = new DeferredRetrySession(job, journal, log, name);
        var fileCount = Math.Max(pending.Count, totals.Files);
        var totalBytes = totals.Bytes;
        var adaptiveOn = job.Options.AdaptiveCopy;
        var heartbeat = Stopwatch.StartNew();
        var list = pending.ToList();
        for (var index = 0; index < list.Count; index++)
        {
            var file = list[index];
            PulseDestination(job, reporter, file);
            cancellationToken.ThrowIfCancellationRequested();
            await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
            await WaitForHoursAsync(job, pause, log, name, reporter, cloud, cancellationToken).ConfigureAwait(false);
            if (job.HeaderCopyModeChosen && job.HeaderCopyMode is not (HeaderCopyMode.FollowSaved or HeaderCopyMode.OneStream))
            {
                var rest = list.Skip(index).ToList();
                var largestPending = rest.Count == 0 ? 0 : rest.Max(f => f.Size);
                var switched = await AdaptiveCopyPlanner.PrepareAsync(
                        job, budget, rest, overlapPlan?.HasPacking == true, largestPending, log, name,
                        (label, current) => reporter.ShowStatus(label, current),
                        cancellationToken)
                    .ConfigureAwait(false);
                await CopyBalancedAsync(
                        job, journal, budget, pause, log, name, reporter, cloud, speed, rest, totals,
                        cancellationToken, io, markUnpacked, switched, overlapPlan)
                    .ConfigureAwait(false);
                return;
            }

            var fileStatus = adaptiveOn ? AdaptiveCopyPolicy.CopyingOneStream : $"Copying {file.RelativePath}";
            try
            {
                if (ShouldSkip(file, job.Options))
                {
                    reporter.Update(fileStatus, file.RelativePath, speed);
                    TryJournal(journal, () => journal.MarkSkipped(file.RelativePath, "Destination is newer or equal"), log, job.Id, name);
                    log.Info(job.Id, name, $"Skip {file.RelativePath} (dest newer or equal)");
                    continue;
                }

                pause.BeginFile(file.RelativePath, file.Size);
                reporter.Update(fileStatus, file.RelativePath, speed);
                if (adaptiveOn && heartbeat.Elapsed.TotalSeconds >= AdaptiveCopyPolicy.HeartbeatSeconds)
                {
                    heartbeat.Restart();
                    log.Info(job.Id, name, AdaptiveCopyLog.Heartbeat(
                        AdaptiveCopyPolicy.CopyingOneStream,
                        pause.InFlightPaths(),
                        speed.WindowBytesPerSecond,
                        speed.StageBytesPerSecond));
                }
                var copied = await CopyWithRetriesAsync(job, file, journal, budget, pause, log, name, speed, cancellationToken, io)
                    .ConfigureAwait(false);
                pause.EndFile();
                if (copied.ok)
                {
                    if (!TryJournal(journal, () =>
                        {
                            journal.MarkCopied(file.RelativePath, copied.hash);
                            if (markUnpacked)
                            {
                                journal.MarkUnpacked(file.RelativePath);
                            }
                        }, log, job.Id, name))
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
        TransferRundown.MarkPaused(job);
        TryJournal(journal, () => journal.SaveJob(job), log, job.Id, name);
        log.Info(job.Id, name, $"Paused after completing {relativePath}.");
        reporter.Update($"Paused after completing {relativePath}");
        await pause.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);
        if (job.Status == JobStatus.Paused)
        {
            job.Status = JobStatus.Copying;
            TransferRundown.MarkUnpaused(job);
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
            try
            {
            PulseDestination(job, reporter, file);
            pause.BeginFile(file.RelativePath, file.Size);
            reporter.Update($"Retrying deferred {file.RelativePath}", file.RelativePath, speed);
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
        UnbufferedIoSession? io = null,
        IReadOnlyList<FileRecord>? onlyThese = null,
        bool copyLoose = true,
        CompressionLevel compression = CompressionLevel.NoCompression)
    {
        var zipPath = ZipPack.ZipPath(mapping);
        var files = journal.GetFiles().Where(f => f.Status != FileCopyStatus.Skipped).ToList();
        if (onlyThese is not null)
        {
            var keys = new HashSet<string>(onlyThese.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
            files = files.Where(f => keys.Contains(f.RelativePath)).ToList();
        }

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
        if (!copyLoose)
        {
            if (onlyThese is not null)
            {
                packable = pending;
            }

            loose = [];
        }

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
                                await ZipPack.PackOneEntryAsync(
                                        zip, file, job, budget, pause, speed, buffer, hashes, cancellationToken, compression, mapping.DestRoot)
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
                    },
                    compression: compression,
                    destRoot: mapping.DestRoot).ConfigureAwait(false);

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

            try
            {
                if (ShouldSkip(file, job.Options))
                {
                    reporter?.Update($"Copying {file.RelativePath} (already compressed)", file.RelativePath, speed);
                    TryJournal(journal, () => journal.MarkSkipped(file.RelativePath, "Destination is newer or equal"), log, job.Id, name);
                    log.Info(job.Id, name, $"Skip {file.RelativePath} (already compressed, dest newer or equal)");
                    continue;
                }

                if (reporter is not null)
                {
                    PulseDestination(job, reporter, file);
                }

                pause.BeginFile(file.RelativePath, file.Size);
                reporter?.Update($"Copying {file.RelativePath} (already compressed)", file.RelativePath, speed);
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
                    if (!JobHeartbeat.HasCopyProgress(percent, pause.CurrentFilePath, job)
                        && totals.DoneFiles == 0
                        && totals.DoneBytes == 0)
                    {
                        continue;
                    }

                    JobHeartbeat.Write(journal, job, percent, pause.CurrentFilePath);
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
                TransferRundown.MarkPaused(job);
                log.Info(job.Id, name, $"Paused — outside hours ({job.Options.HoursStart:HH:mm}–{job.Options.HoursEnd:HH:mm}).");
                Report(progress, job, name, cloud, "Paused — outside hours");
                job.Status = previous;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        if (logged)
        {
            job.Status = JobStatus.Copying;
            TransferRundown.MarkUnpaused(job);
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
    public const double RecentWindowSeconds = 8;
    public const long ThinWindowBytes = 4L * 1024 * 1024;

    private readonly object _lock = new();
    private Func<long>? _now;
    private long _bytes;
    private bool _stageOpen;
    private long _stageBytes;
    private long _stageStart;
    private long _closedBytes;
    private double _closedSeconds;
    private long _windowBytes;
    private long _windowStart;
    private long _lastWindowBytes;
    private double _lastWindowBps;
    private double _publishedBps;
    private double _heldBps;

    public long Bytes => Interlocked.Read(ref _bytes);

    public double BytesPerSecond => EffectiveBytesPerSecond;

    /// <summary>Recent window, or the stage rate when that window is only a gap between files.</summary>
    public double EffectiveBytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                if (_publishedBps >= 1)
                {
                    return _publishedBps;
                }

                var stage = StageUnlocked();
                return stage >= 1 ? stage : 0;
            }
        }
    }

    public double WindowBytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                var elapsed = Elapsed(_windowStart, NowTicks());
                if (elapsed >= 1 && _windowBytes > 0)
                {
                    return _windowBytes / elapsed;
                }

                if (_lastWindowBps >= 1)
                {
                    return _lastWindowBps;
                }

                return StageUnlocked();
            }
        }
    }

    public double StageBytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                return StageUnlocked();
            }
        }
    }

    /// <summary>Start the copy-stage clock. Probe bytes are not included. Does not run on a mode change.</summary>
    public void BeginStage()
    {
        lock (_lock)
        {
            CloseOpenStage();
            _stageOpen = true;
            _stageBytes = 0;
            _stageStart = NowTicks();
            _windowBytes = 0;
            _windowStart = _stageStart;
            _lastWindowBytes = 0;
            _lastWindowBps = 0;
            _publishedBps = 0;
            _heldBps = 0;
        }
    }

    /// <summary>
    /// Bytes written and the time those writes took. A stage that wrote nothing (the probe) is left out.
    /// <paramref name="bytesAlreadySaved"/> and <paramref name="secondsAlreadySaved"/> are earlier runs of this job.
    /// </summary>
    public void ApplyTo(Job job, long bytesAlreadySaved, double secondsAlreadySaved)
    {
        var pace = Pace();
        job.TransferBytes = Math.Max(0, bytesAlreadySaved) + pace.Bytes;
        job.TransferSeconds = Math.Max(0, secondsAlreadySaved) + pace.Seconds;
        job.AverageBytesPerSecond = TransferRundown.AverageBytesPerSecond(
            job.TransferBytes,
            TimeSpan.FromSeconds(job.TransferSeconds));
    }

    /// <summary>This tracker's writes only. Earlier runs are added in <see cref="ApplyTo"/>.</summary>
    public (long Bytes, double Seconds) Pace()
    {
        lock (_lock)
        {
            var bytes = _closedBytes;
            var seconds = _closedSeconds;
            if (_stageOpen && _stageBytes > 0)
            {
                bytes += _stageBytes;
                var elapsed = Elapsed(_stageStart, NowTicks());
                if (elapsed > 0)
                {
                    seconds += elapsed;
                }
            }

            return (bytes, seconds);
        }
    }

    private void CloseOpenStage()
    {
        if (!_stageOpen || _stageBytes <= 0)
        {
            return;
        }

        _closedBytes += _stageBytes;
        var elapsed = Elapsed(_stageStart, NowTicks());
        if (elapsed > 0)
        {
            _closedSeconds += elapsed;
        }
    }

    internal void SetClock(Func<long> now) => _now = now;

    public void Add(long count)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _bytes, count);
        lock (_lock)
        {
            if (!_stageOpen)
            {
                _stageOpen = true;
                _stageStart = NowTicks();
                _windowStart = _stageStart;
            }

            _stageBytes += count;
            _windowBytes += count;
            var now = NowTicks();
            var stageElapsed = Elapsed(_stageStart, now);
            var windowElapsed = Elapsed(_windowStart, now);
            var stageBps = stageElapsed >= 3 && _stageBytes > 0 ? _stageBytes / stageElapsed : 0;
            if (windowElapsed >= RecentWindowSeconds)
            {
                var windowBps = _windowBytes / Math.Max(windowElapsed, 0.001);
                _lastWindowBps = windowBps;
                _lastWindowBytes = _windowBytes;
                _publishedBps = ChooseDisplayedRate(windowBps, _windowBytes, stageBps, _heldBps);
                if (_publishedBps >= 1)
                {
                    _heldBps = _publishedBps;
                }

                _windowBytes = 0;
                _windowStart = now;
            }
            else if (_lastWindowBytes < ThinWindowBytes && stageBps >= 1)
            {
                _publishedBps = stageBps;
                _heldBps = stageBps;
            }
        }
    }

    /// <summary>
    /// A window that only caught a gap (a few MB or less) must not replace the stage rate.
    /// A full window of a real slowdown still shows that slower rate.
    /// </summary>
    internal static double ChooseDisplayedRate(double windowBps, long windowBytes, double stageBps, double heldBps)
    {
        if (windowBytes < ThinWindowBytes)
        {
            if (stageBps >= 1)
            {
                return stageBps;
            }

            if (heldBps >= 1)
            {
                return heldBps;
            }

            return 0;
        }

        if (windowBps >= 1)
        {
            return windowBps;
        }

        if (stageBps >= 1)
        {
            return stageBps;
        }

        return heldBps >= 1 ? heldBps : 0;
    }

    private double StageUnlocked()
    {
        if (!_stageOpen || _stageBytes <= 0)
        {
            return 0;
        }

        var elapsed = Elapsed(_stageStart, NowTicks());
        return elapsed < 0.001 ? 0 : _stageBytes / elapsed;
    }

    private long NowTicks() => _now?.Invoke() ?? Stopwatch.GetTimestamp();

    private static double Elapsed(long start, long now) =>
        now <= start ? 0 : (now - start) / (double)Stopwatch.Frequency;
}
