using System.Collections.Concurrent;

namespace Mercury;

public sealed class JobScheduler : IDisposable
{
    private readonly AppPaths _paths;
    private readonly ICopyEngine _engine;
    private readonly IRundownCapture _rundownCapture;
    private readonly ConcurrentDictionary<string, Running> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RundownWork> _rundowns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JobProgress> _progress = new(StringComparer.Ordinal);
    private readonly object _queueLock = new();
    private readonly List<Job> _queue = [];
    private readonly CancellationTokenSource _lifetime = new();
    private int _pumping;
    private int _drain;
    private string? _forceStartId;

    public JobScheduler(AppPaths paths, ICopyEngine? engine = null, IRundownCapture? rundown = null)
    {
        _paths = paths;
        _engine = engine ?? new CopyEngine();
        _rundownCapture = rundown ?? DefaultRundownCapture.Instance;
        Log = new FileJobLog(paths);
        Budget = new BandwidthBudget();
        Budget.Apply(AppSettingsStore.Load(paths));
        LoadQueue();
        _ = ScheduleLoopAsync(_lifetime.Token);
    }

    /// <summary>One copy at a time in v1. Background rundown does not consume this slot.</summary>
    public int MaxConcurrentJobs { get; set; } = 1;
    public BandwidthBudget Budget { get; }
    public PauseGate GlobalPause { get; } = new();
    public FileJobLog Log { get; }
    public AppPaths Paths => _paths;

    public event EventHandler<JobProgress>? ProgressChanged;
    public event EventHandler? QueueChanged;
    public event EventHandler? HistoryChanged;

    public IReadOnlyList<Job> Queue
    {
        get
        {
            lock (_queueLock)
            {
                return _queue.ToList();
            }
        }
    }

    public IReadOnlyList<string> RunningJobIds => _running.Keys.ToList();

    public IReadOnlyList<string> RundownJobIds => _rundowns.Keys.ToList();

    public bool HasRunningJob => !_running.IsEmpty;

    public bool HasBackgroundRundown => !_rundowns.IsEmpty;

    /// <summary>True while a copy is running. Rundown in the background does not block Start.</summary>
    public bool BlocksStart =>
        _running.Values.Any(r => !r.Cts.IsCancellationRequested);

    public Job? TryGetRunningJob(string? jobId = null)
    {
        if (jobId is not null && _running.TryGetValue(jobId, out var named))
        {
            return named.Job;
        }

        return _running.Values.Select(r => r.Job).FirstOrDefault();
    }

    public Job? TryGetRundownJob(string? jobId = null)
    {
        if (jobId is not null && _rundowns.TryGetValue(jobId, out var named))
        {
            return named.Job;
        }

        return _rundowns.Values.Select(r => r.Job).FirstOrDefault();
    }

    public void Enqueue(Job job, bool startNow = false)
    {
        EnsureName(job);
        if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Incomplete)
        {
            job.Status = JobStatus.Pending;
        }

        lock (_queueLock)
        {
            if (_running.ContainsKey(job.Id) || _rundowns.ContainsKey(job.Id))
            {
                return;
            }

            var existing = _queue.FindIndex(j => j.Id == job.Id);
            if (existing >= 0)
            {
                _queue[existing] = job;
            }
            else
            {
                _queue.Add(job);
            }

            if (startNow)
            {
                job.OnHold = false;
                _forceStartId = job.Id;
            }

            SaveQueue();
        }

        RaiseQueueChanged();
        if (startNow)
        {
            Kick();
        }
    }

    public void Remove(string jobId)
    {
        if (_running.ContainsKey(jobId) || _rundowns.ContainsKey(jobId))
        {
            Stop(jobId);
        }

        lock (_queueLock)
        {
            _queue.RemoveAll(j => j.Id == jobId);
            SaveQueue();
        }

        RaiseQueueChanged();
    }

    public void Move(string jobId, int delta)
    {
        lock (_queueLock)
        {
            var i = _queue.FindIndex(j => j.Id == jobId);
            var n = i + delta;
            if (i < 0 || n < 0 || n >= _queue.Count)
            {
                return;
            }

            (_queue[i], _queue[n]) = (_queue[n], _queue[i]);
            SaveQueue();
        }

        RaiseQueueChanged();
    }

    public void ResumeOrRetry(string jobId)
    {
        if (_running.ContainsKey(jobId))
        {
            ResumePaused(jobId);
            return;
        }

        lock (_queueLock)
        {
            var job = _queue.FirstOrDefault(j => j.Id == jobId);
            if (job is null)
            {
                return;
            }

            job.OnHold = false;
            if (job.Status is JobStatus.Cancelled or JobStatus.Incomplete or JobStatus.Failed
                or JobStatus.Paused or JobStatus.PausedOutsideHours)
            {
                job.Status = JobStatus.Pending;
            }

            if (job.Status == JobStatus.Pending)
            {
                _forceStartId = job.Id;
            }

            SaveQueue();
        }

        RaiseQueueChanged();
        Kick();
    }

    public void SetHold(string jobId, bool hold)
    {
        lock (_queueLock)
        {
            var job = _queue.FirstOrDefault(j => j.Id == jobId);
            if (job is null)
            {
                return;
            }

            if (hold && job.Status != JobStatus.Pending)
            {
                return;
            }

            job.OnHold = hold;
            if (hold && _forceStartId == jobId)
            {
                _forceStartId = null;
            }

            SaveQueue();
        }

        RaiseQueueChanged();
        if (!hold)
        {
            Kick();
        }
    }

    public (int Index, int Count) QueueOrdinal(string? jobId)
    {
        lock (_queueLock)
        {
            var count = Math.Max(1, _queue.Count);
            if (_queue.Count == 0)
            {
                return (1, 1);
            }

            if (!string.IsNullOrEmpty(jobId))
            {
                var i = _queue.FindIndex(j => j.Id == jobId);
                if (i >= 0)
                {
                    return (i + 1, _queue.Count);
                }
            }

            var active = _queue.FindIndex(j =>
                j.Status is JobStatus.Preparing or JobStatus.Enumerating or JobStatus.Copying
                    or JobStatus.Verifying or JobStatus.Paused or JobStatus.PausedOutsideHours
                || _rundowns.ContainsKey(j.Id));
            if (active >= 0)
            {
                return (active + 1, _queue.Count);
            }

            return (1, _queue.Count);
        }
    }

    public void PersistJob(Job job)
    {
        if (_running.TryGetValue(job.Id, out var running))
        {
            running.Journal.SaveJob(running.Job);
        }
        else if (_rundowns.TryGetValue(job.Id, out var rundown))
        {
            rundown.Journal.SaveJob(rundown.Job);
        }

        lock (_queueLock)
        {
            SaveQueue();
        }
    }

    public void Kick(bool drain = false)
    {
        if (drain)
        {
            Interlocked.Exchange(ref _drain, 1);
        }

        if (Interlocked.CompareExchange(ref _pumping, 1, 0) != 0)
        {
            return;
        }

        _ = PumpAsync();
    }

    public async Task StartAsync(Job job, bool resumeJournal, CancellationToken cancellationToken)
    {
        await RunCopyCoreAsync(job, resumeJournal, cancellationToken).ConfigureAwait(false);
        await WaitForRundownAsync(job.Id).ConfigureAwait(false);
    }

    private async Task RunCopyCoreAsync(Job job, bool resumeJournal, CancellationToken cancellationToken)
    {
        if (_running.Count >= MaxConcurrentJobs)
        {
            throw new InvalidOperationException(
                $"v1 runs one copy at a time ({MaxConcurrentJobs} concurrent). Stop the current copy or wait for it to finish.");
        }

        EnsureName(job);

        var remapped = VolumeInfo.RemapIfMissing(job.SourcePath, job.VolumeSerial);
        if (!string.IsNullOrEmpty(remapped))
        {
            job.SourcePath = remapped;
        }

        var jobDir = _paths.JobDirectory(job.Id);
        JobJournal journal;
        if (resumeJournal && JobJournal.Exists(jobDir))
        {
            journal = JobJournal.Open(jobDir);
            journal.SaveJob(job);
        }
        else
        {
            Directory.CreateDirectory(jobDir);
            journal = JobJournal.Create(jobDir, job);
        }

        AppSettingsStore.SaveLastJobId(_paths, job.Id);
        var pause = new PauseGate { Parent = GlobalPause };
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new Running(job, journal, pause, cts);
        if (!_running.TryAdd(job.Id, running))
        {
            journal.Dispose();
            throw new InvalidOperationException("That job is already running.");
        }

        PruneFinishedProgress();
        if (job.StartedUtc is null)
        {
            TransferRundown.MarkStarted(job);
            journal.SaveJob(job);
        }

        var progress = new Progress<JobProgress>(p => FillProgress(job, journal, p));

        var reportFinal = false;
        var keepHeartbeat = false;
        var rundownOwnsJournal = false;
        try
        {
            Log.Info(job.Id, job.Name, $"Job started. Log: {_paths.JobLogFile(job.Id)}");
            await _engine.RunAsync(job, journal, Budget, pause, Log, progress, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.ResultMessage = "Stopped. Progress is saved; you can Resume.";
            Log.Info(job.Id, job.Name, job.ResultMessage);
            reportFinal = true;
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.ResultMessage = ex.Message;
            Log.Error(job.Id, job.Name, ex.ToString());
            reportFinal = true;
        }
        finally
        {
            if (_running.TryRemove(job.Id, out var finished))
            {
                keepHeartbeat = finished.KeepHeartbeatOnCancel;
            }

            try
            {
                if (job.Status is JobStatus.Completed or JobStatus.Incomplete or JobStatus.Failed
                    || (job.Status == JobStatus.Cancelled && !keepHeartbeat))
                {
                    JobHeartbeat.Clear(journal);
                }
                else
                {
                    var hb = SafeTotals(journal);
                    var percent = hb.Bytes > 0 ? 100.0 * hb.DoneBytes / hb.Bytes : 0;
                    JobHeartbeat.Write(journal, job.Id, percent, null);
                }
            }
            catch
            {
                // heartbeat must not hide the real result
            }

            cts.Dispose();
            BeginRundown(job, journal, progress);
            rundownOwnsJournal = true;
            lock (_queueLock)
            {
                SaveQueue();
            }

            RaiseQueueChanged();
        }

        if (reportFinal)
        {
            ReportFinal(job);
        }

        if (!rundownOwnsJournal)
        {
            journal.Dispose();
        }
    }

    private async Task WaitForRundownAsync(string jobId)
    {
        if (_rundowns.TryGetValue(jobId, out var work))
        {
            await work.Task.ConfigureAwait(false);
        }
    }

    public void Pause(string jobId)
    {
        if (_running.TryGetValue(jobId, out var running))
        {
            running.Pause.Pause();
            running.Job.Status = JobStatus.Paused;
            running.Journal.SaveJob(running.Job);
            Log.Info(jobId, running.Job.Name, "Paused.");
            ReportFinal(running.Job);
        }
    }

    public void RequestPauseAfterFile(string jobId)
    {
        if (_running.TryGetValue(jobId, out var running))
        {
            running.Pause.RequestPauseAfterFile();
            Log.Info(jobId, running.Job.Name, "Pause after this file requested.");
        }
    }

    public void CancelPauseAfterFile(string jobId)
    {
        if (_running.TryGetValue(jobId, out var running) && running.Pause.PauseAfterFileRequested)
        {
            running.Pause.CancelPauseAfterFile();
            Log.Info(jobId, running.Job.Name, "Pause after this file cancelled.");
        }
    }

    public bool IsPauseAfterFilePending(string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || !_running.TryGetValue(jobId, out var running))
        {
            return false;
        }

        return running.Pause.PauseAfterFileRequested;
    }

    public TimeSpan? EstimateCurrentFileEta(string jobId)
    {
        if (!_running.TryGetValue(jobId, out var running))
        {
            return TimeSpan.Zero;
        }

        var bps = 0d;
        long copied = 0;
        var elapsed = TimeSpan.Zero;
        if (_progress.TryGetValue(jobId, out var p))
        {
            bps = p.BytesPerSecond;
            copied = p.BytesCopied;
            if (p.StartedUtc is { } start)
            {
                elapsed = DateTimeOffset.UtcNow - start;
            }
        }

        bps = ByteFormatter.EffectiveRate(bps, copied, elapsed);
        return running.Pause.RemainingFileEta(bps);
    }

    public bool CanPauseAfterFile(string jobId) =>
        _running.TryGetValue(jobId, out var running)
        && running.Job.Status == JobStatus.Copying
        && !running.Pause.IsEffectivelyPaused;

    public void PauseAll()
    {
        GlobalPause.Pause();
        foreach (var running in _running.Values)
        {
            running.Job.Status = JobStatus.Paused;
            running.Journal.SaveJob(running.Job);
            Log.Info(running.Job.Id, running.Job.Name, "Paused (all jobs).");
            ReportFinal(running.Job);
        }
    }

    public void ResumeAll()
    {
        GlobalPause.Resume();
        foreach (var running in _running.Values)
        {
            if (running.Pause.IsPaused)
            {
                continue;
            }

            running.Job.Status = JobStatus.Copying;
            running.Journal.SaveJob(running.Job);
            Log.Info(running.Job.Id, running.Job.Name, "Resumed (all jobs).");
            ReportFinal(running.Job);
        }

        Kick(drain: true);
    }

    public void ResumePaused(string jobId)
    {
        if (_running.TryGetValue(jobId, out var running))
        {
            running.Job.Status = JobStatus.Copying;
            running.Journal.SaveJob(running.Job);
            running.Pause.Resume();
            Log.Info(jobId, running.Job.Name, "Resumed.");
        }
    }

    public void Stop(string jobId, bool clearHeartbeat = true)
    {
        if (_running.TryGetValue(jobId, out var running))
        {
            running.KeepHeartbeatOnCancel = !clearHeartbeat;
            running.Pause.Resume();
            if (_running.Count <= 1)
            {
                GlobalPause.Resume();
            }

            running.Cts.Cancel();
            if (running.Job.Status is not JobStatus.Completed and not JobStatus.Failed
                and not JobStatus.Incomplete and not JobStatus.Cancelled)
            {
                running.Job.Status = JobStatus.Cancelled;
                running.Job.ResultMessage = "Stopping…";
            }

            ReportFinal(running.Job);
            return;
        }

        if (_rundowns.TryGetValue(jobId, out var rundown))
        {
            rundown.Cts.Cancel();
        }
    }

    public void StopAll(bool clearHeartbeat = true)
    {
        foreach (var id in _running.Keys.ToList())
        {
            Stop(id, clearHeartbeat);
        }

        foreach (var id in _rundowns.Keys.ToList())
        {
            Stop(id, clearHeartbeat);
        }
    }

    public Job? TryLoadLastJob()
    {
        var id = AppSettingsStore.LoadLastJobId(_paths);
        return id is null ? null : TryLoadJob(id);
    }

    public Job? TryLoadJob(string jobId)
    {
        var dir = _paths.JobDirectory(jobId);
        if (!JobJournal.Exists(dir))
        {
            return null;
        }

        try
        {
            using var journal = JobJournal.Open(dir);
            return journal.LoadJob();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Journal totals + heartbeat for the Progress header after a restart.</summary>
    public JobProgress? TrySnapshotProgress(Job job)
    {
        try
        {
            var dir = _paths.JobDirectory(job.Id);
            FileTotals totals = default;
            JobHeartbeatState? heartbeat = null;
            if (JobJournal.Exists(dir))
            {
                using var journal = JobJournal.Open(dir);
                totals = journal.Totals();
                heartbeat = JobHeartbeat.Read(journal, job.Id);
                if (totals.Files > job.SourceFiles)
                {
                    job.SourceFiles = totals.Files;
                }

                if (totals.DoneFiles > job.DestFiles)
                {
                    job.DestFiles = totals.DoneFiles;
                }

                if (totals.DoneBytes > job.BytesCopied)
                {
                    job.BytesCopied = totals.DoneBytes;
                }
            }

            if (totals.Files == 0 && job.SourceFiles == 0 && job.BytesCopied == 0)
            {
                return null;
            }

            return new JobProgress
            {
                JobId = job.Id,
                JobName = string.IsNullOrWhiteSpace(job.Name) ? job.Id : job.Name,
                Status = job.Status,
                CurrentFile = heartbeat?.File,
                BytesCopied = totals.DoneBytes > 0 ? totals.DoneBytes : job.BytesCopied,
                BytesTotal = totals.Bytes,
                FilesCopied = totals.DoneFiles > 0 ? totals.DoneFiles : job.DestFiles,
                FilesTotal = totals.Files > 0 ? totals.Files : job.SourceFiles,
                Message = job.ResultMessage,
                StartedUtc = job.StartedUtc
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Journal file rows for the folder tree (running job first, else last job on disk).</summary>
    public IReadOnlyList<FileRecord> LoadJournalFiles(string? jobId)
    {
        if (!string.IsNullOrEmpty(jobId) && _running.TryGetValue(jobId, out var running))
        {
            try
            {
                return running.Journal.GetFiles();
            }
            catch
            {
                return [];
            }
        }

        if (!string.IsNullOrEmpty(jobId) && _rundowns.TryGetValue(jobId, out var rundown))
        {
            try
            {
                return rundown.Journal.GetFiles();
            }
            catch
            {
                return [];
            }
        }

        var id = jobId ?? AppSettingsStore.LoadLastJobId(_paths);
        if (id is null)
        {
            return [];
        }

        var dir = _paths.JobDirectory(id);
        if (!JobJournal.Exists(dir))
        {
            return [];
        }

        try
        {
            using var journal = JobJournal.Open(dir);
            return journal.GetFiles();
        }
        catch
        {
            return [];
        }
    }

    public JobHeartbeatState? FindDirtyHeartbeat() => JobHeartbeat.FindDirty(_paths);

    private sealed record Running(Job Job, JobJournal Journal, PauseGate Pause, CancellationTokenSource Cts)
    {
        public bool KeepHeartbeatOnCancel { get; set; }
    }

    private sealed class RundownWork
    {
        public required Job Job { get; init; }
        public required JobJournal Journal { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required IProgress<JobProgress> Progress { get; init; }
        public Task Task { get; set; } = Task.CompletedTask;
        public int StageCount { get; init; }
        public DateTimeOffset? StartedUtc { get; init; }
        public DateTimeOffset StageStartedUtc { get; init; } = DateTimeOffset.UtcNow;
        public long BytesCopied { get; init; }
        public long BytesTotal { get; init; }
        public int FilesCopied { get; init; }
        public int FilesTotal { get; init; }
        public string? TypeSummary { get; init; }
        public bool Cloud { get; init; }
    }

    public JobProgress? GetProgress(string jobId) =>
        _progress.TryGetValue(jobId, out var p) ? p : null;

    public JobProgress GetOverallProgress()
    {
        List<Job> jobs;
        lock (_queueLock)
        {
            jobs = _queue.ToList();
        }

        if (jobs.Count == 0)
        {
            var runningOnly = _running.Values.Select(r => r.Job).ToList();
            if (runningOnly.Count == 0)
            {
                return new JobProgress { Status = JobStatus.Pending, Message = "Idle" };
            }

            jobs = runningOnly;
        }

        if (!ProgressHeader.ShowOverall(jobs.Count) && _progress.Count == 1)
        {
            return _progress.Values.First();
        }

        long bytes = 0, total = 0;
        var files = 0;
        var filesTotal = 0;
        double speed = 0;
        var issues = 0;
        DateTimeOffset? started = null;
        var live = false;
        foreach (var job in jobs)
        {
            if (_progress.TryGetValue(job.Id, out var p))
            {
                bytes += p.BytesCopied;
                total += p.BytesTotal;
                files += p.FilesCopied;
                filesTotal += p.FilesTotal;
                speed += p.BytesPerSecond;
                issues += p.IssueCount;
                if (p.StartedUtc is { } s && (started is null || s < started))
                {
                    started = s;
                }

                live |= p.Status is JobStatus.Preparing or JobStatus.Copying or JobStatus.Enumerating
                    or JobStatus.Verifying or JobStatus.Paused or JobStatus.PausedOutsideHours;
            }
            else
            {
                bytes += job.BytesCopied;
                files += job.DestFiles;
                filesTotal += job.SourceFiles;
                issues += job.IssueCount;
                if (job.StartedUtc is { } s && (started is null || s < started))
                {
                    started = s;
                }
            }
        }

        return new JobProgress
        {
            Status = live ? JobStatus.Copying : jobs[0].Status,
            BytesCopied = bytes,
            BytesTotal = total,
            FilesCopied = files,
            FilesTotal = filesTotal,
            BytesPerSecond = speed,
            IssueCount = issues,
            Message = jobs.Count + " jobs",
            Eta = EstimateEta(bytes, total, speed),
            StartedUtc = started
        };
    }

    private async Task PumpAsync()
    {
        var allowUnscheduled = Interlocked.Exchange(ref _drain, 0) == 1;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                Job? next;
                lock (_queueLock)
                {
                    if (_running.Count >= MaxConcurrentJobs || GlobalPause.IsPaused)
                    {
                        break;
                    }

                    if (_forceStartId is not null)
                    {
                        var forced = _queue.FirstOrDefault(j => j.Id == _forceStartId);
                        if (forced is null || forced.OnHold || forced.Status != JobStatus.Pending)
                        {
                            _forceStartId = null;
                        }
                    }

                    var forceId = _forceStartId;
                    next = JobDue.FindNext(
                        _queue,
                        DateTimeOffset.Now,
                        forceId,
                        includeUnscheduled: allowUnscheduled || forceId is not null);
                    if (next is not null && forceId == next.Id)
                    {
                        _forceStartId = null;
                    }

                    if (next is null)
                    {
                        break;
                    }
                }

                var resume = JobJournal.Exists(_paths.JobDirectory(next.Id));
                try
                {
                    await RunCopyCoreAsync(next, resume, _lifetime.Token).ConfigureAwait(false);
                    allowUnscheduled = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    next.Status = JobStatus.Failed;
                    next.ResultMessage = ex.Message;
                    lock (_queueLock)
                    {
                        SaveQueue();
                    }

                    RaiseQueueChanged();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _pumping, 0);
            var again = false;
            lock (_queueLock)
            {
                again = !_lifetime.IsCancellationRequested
                        && _running.Count < MaxConcurrentJobs
                        && !GlobalPause.IsPaused
                        && JobDue.FindNext(
                            _queue,
                            DateTimeOffset.Now,
                            _forceStartId,
                            includeUnscheduled: allowUnscheduled || _forceStartId is not null) is not null;
            }

            if (again)
            {
                Kick(drain: allowUnscheduled);
            }
        }
    }

    private async Task ScheduleLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Kick();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void LoadQueue()
    {
        lock (_queueLock)
        {
            _queue.Clear();
            foreach (var job in QueueStore.Load(_paths))
            {
                if (job.Status is JobStatus.Preparing or JobStatus.Copying or JobStatus.Enumerating or JobStatus.Verifying
                    or JobStatus.Paused or JobStatus.PausedOutsideHours)
                {
                    job.Status = JobStatus.Pending;
                }

                _queue.Add(job);
            }
        }
    }

    private void SaveQueue()
    {
        QueueStore.Save(_paths, _queue);
    }

    private void RaiseQueueChanged() => QueueChanged?.Invoke(this, EventArgs.Empty);

    private static void EnsureName(Job job)
    {
        if (!string.IsNullOrWhiteSpace(job.Name))
        {
            return;
        }

        job.Name = Path.GetFileName(job.SourcePath.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(job.Name))
        {
            job.Name = job.Id[..8];
        }
    }

    private void ReportFinal(Job job)
    {
        var p = new JobProgress
        {
            JobId = job.Id,
            JobName = job.Name,
            Status = job.Status,
            Message = job.ResultMessage,
            IssueCount = job.IssueCount,
            StartedUtc = job.StartedUtc,
            StageIndex = 0,
            StageCount = 0
        };
        _progress[job.Id] = p;
        ProgressChanged?.Invoke(this, p);
    }

    private void PruneFinishedProgress()
    {
        foreach (var key in _progress.Keys.ToList())
        {
            if (!_running.ContainsKey(key) && !_rundowns.ContainsKey(key))
            {
                _progress.TryRemove(key, out _);
            }
        }
    }

    private void FillProgress(Job job, JobJournal journal, JobProgress p)
    {
        var rundown = p.IsRundownStage;
        var totals = SafeTotals(journal);
        var copied = Math.Max(p.BytesCopied, totals.DoneBytes);
        var total = Math.Max(p.BytesTotal, totals.Bytes);
        var started = p.StartedUtc ?? job.StartedUtc;
        var elapsed = started is { } startAt ? DateTimeOffset.UtcNow - startAt : TimeSpan.Zero;
        var rate = ByteFormatter.EffectiveRate(p.BytesPerSecond, copied, elapsed);
        var filled = new JobProgress
        {
            JobId = p.JobId,
            JobName = p.JobName,
            Status = p.Status,
            CurrentFile = p.CurrentFile,
            Message = p.Message,
            CloudDestination = p.CloudDestination,
            BytesCopied = copied,
            BytesTotal = total,
            FilesCopied = rundown ? p.FilesCopied : Math.Max(p.FilesCopied, totals.DoneFiles),
            FilesTotal = rundown ? p.FilesTotal : Math.Max(p.FilesTotal, totals.Files),
            IssueCount = Math.Max(p.IssueCount, totals.Failed),
            BytesPerSecond = rundown ? 0 : rate,
            Eta = rundown ? p.Eta : EstimateEta(copied, total, rate),
            StageIndex = p.StageIndex,
            StageCount = p.StageCount,
            StageName = p.StageName,
            StartedUtc = started,
            StageStartedUtc = p.StageStartedUtc,
            TypeSummary = p.TypeSummary,
            RundownDone = p.RundownDone,
            RundownTotal = p.RundownTotal,
            RundownPerSecond = p.RundownPerSecond
        };
        _progress[job.Id] = filled;
        ProgressChanged?.Invoke(this, filled);
    }

    private void BeginRundown(Job job, JobJournal journal, IProgress<JobProgress> progress)
    {
        var last = GetProgress(job.Id);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var work = new RundownWork
        {
            Job = job,
            Journal = journal,
            Cts = cts,
            Progress = progress,
            StageCount = last is { StageCount: > 0 } ? last.StageCount : CopyPipeline.For(job, true, job.Options.PackAsZip).Count,
            StartedUtc = last?.StartedUtc ?? job.StartedUtc,
            StageStartedUtc = DateTimeOffset.UtcNow,
            BytesCopied = last?.BytesCopied ?? job.BytesCopied,
            BytesTotal = last?.BytesTotal ?? 0,
            FilesCopied = last?.FilesCopied ?? job.DestFiles,
            FilesTotal = last?.FilesTotal ?? job.SourceFiles,
            TypeSummary = last?.TypeSummary,
            Cloud = last?.CloudDestination ?? false
        };
        work.Task = Task.Run(() => ExecuteRundown(work));
        _rundowns[job.Id] = work;
    }

    private void ExecuteRundown(RundownWork work)
    {
        var job = work.Job;
        var name = string.IsNullOrWhiteSpace(job.Name) ? job.Id[..8] : job.Name;
        var token = work.Cts.Token;
        try
        {
            Log.Info(job.Id, name, "Rundown running in background");
            ReportRundown(work, new RundownProgress(0, Math.Max(1, job.SourceFiles), 0, null, "destination"));
            CopyMapping? mapping = null;
            try
            {
                mapping = CopyShape.Resolve(job.SourcePath, job.DestinationPath, job.Options.IncludeSourceFolderName);
            }
            catch
            {
                mapping = null;
            }

            _rundownCapture.Capture(job, work.Journal, mapping, Log, name, token, p => ReportRundown(work, p));
        }
        catch (OperationCanceledException)
        {
            // Capture already saved a stopped summary when it can
        }
        catch (Exception ex)
        {
            Log.Error(job.Id, name, ex.ToString());
        }
        finally
        {
            try
            {
                work.Journal.Dispose();
            }
            catch
            {
                // journal already closed
            }

            try
            {
                work.Cts.Dispose();
            }
            catch
            {
                // ignore
            }

            _rundowns.TryRemove(job.Id, out _);
            if (job.Status is JobStatus.Completed or JobStatus.Incomplete or JobStatus.Cancelled or JobStatus.Failed)
            {
                try
                {
                    HistoryStore.Record(_paths, job);
                    HistoryChanged?.Invoke(this, EventArgs.Empty);
                }
                catch
                {
                    // history is best-effort; the job itself already finished
                }
            }

            lock (_queueLock)
            {
                SaveQueue();
            }

            RaiseQueueChanged();
            ReportFinal(job);
        }
    }

    private void ReportRundown(RundownWork work, RundownProgress pulse)
    {
        var job = work.Job;
        var name = string.IsNullOrWhiteSpace(job.Name) ? job.Id[..8] : job.Name;
        var total = Math.Max(pulse.Total, Math.Max(pulse.Done, 1));
        work.Progress.Report(new JobProgress
        {
            JobId = job.Id,
            JobName = name,
            Status = job.Status,
            Message = CopyPipeline.RundownMessage,
            CloudDestination = work.Cloud,
            BytesCopied = work.BytesCopied,
            BytesTotal = work.BytesTotal,
            FilesCopied = work.FilesCopied,
            FilesTotal = work.FilesTotal,
            IssueCount = job.IssueCount,
            Eta = pulse.Eta,
            StageIndex = work.StageCount,
            StageCount = work.StageCount,
            StageName = CopyPipeline.RundownLabel,
            StartedUtc = work.StartedUtc,
            StageStartedUtc = work.StageStartedUtc,
            TypeSummary = work.TypeSummary,
            RundownDone = pulse.Done,
            RundownTotal = total,
            RundownPerSecond = pulse.UnitsPerSecond
        });
    }

    private static FileTotals SafeTotals(JobJournal journal)
    {
        try
        {
            return journal.Totals();
        }
        catch
        {
            return default;
        }
    }

    private static TimeSpan? EstimateEta(long done, long total, double bps)
    {
        if (bps <= 1 || total <= done)
        {
            return null;
        }

        return TimeSpan.FromSeconds((total - done) / bps);
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        StopAll();
        var tasks = _rundowns.Values.Select(w => w.Task).ToArray();
        if (tasks.Length > 0)
        {
            try
            {
                Task.WaitAll(tasks, TimeSpan.FromSeconds(3));
            }
            catch
            {
                // shutting down
            }
        }

        Log.Dispose();
        _lifetime.Dispose();
    }
}
