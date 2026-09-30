using System.Collections.Concurrent;

namespace Mercury;

public sealed class JobScheduler : IDisposable
{
    private readonly AppPaths _paths;
    private readonly ICopyEngine _engine;
    private readonly IRundownCapture _rundownCapture;
    private readonly ConcurrentDictionary<string, Running> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Running> _compares = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RundownWork> _rundowns = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JobProgress> _progress = new(StringComparer.Ordinal);
    private readonly object _queueLock = new();
    private readonly List<Job> _queue = [];
    private readonly CancellationTokenSource _lifetime = new();
    private int _pumping;
    private int _drain;
    private int _kickAgain;
    private int _comparePumping;
    private int _compareDrain;
    private int _compareKickAgain;
    private string? _forceStartId;
    private string? _forceCompareId;

    public JobScheduler(AppPaths paths, ICopyEngine? engine = null, IRundownCapture? rundown = null)
    {
        _paths = paths;
        _engine = engine ?? new CopyEngine();
        _rundownCapture = rundown ?? DefaultRundownCapture.Instance;
        Log = new FileJobLog(paths);
        Budget = new BandwidthBudget();
        Budget.Apply(AppSettingsStore.Load(paths));
        AdaptiveCopyMemory.Use(_paths.DataRoot);
        LoadQueue();
        _ = ScheduleLoopAsync(_lifetime.Token);
    }

    /// <summary>One copy at a time in v1. Background rundown does not consume this slot.</summary>
    public int MaxConcurrentJobs { get; set; } = 1;
    public BandwidthBudget Budget { get; }
    public PauseGate GlobalPause { get; } = new();
    /// <summary>Pause for the compare lane only. A transfer pause does not block a compare.</summary>
    public PauseGate CompareLanePause { get; } = new();
    public FileJobLog Log { get; }
    public AppPaths Paths => _paths;
    private HeaderCopyMode _pendingHeaderCopy = HeaderCopyMode.FollowSaved;
    private HeaderCopyMode _pendingHeaderFrom = HeaderCopyMode.Adaptive;
    private int _pendingManualWidth;
    private int _pendingManualFromWidth;

    /// <summary>
    /// Header checkbox for the current copy. Does not change the saved Adaptive copy option.
    /// Cleared when the copy ends so the next job follows its own saved option.
    /// </summary>
    public void SetHeaderCopyMode(HeaderCopyMode mode, HeaderCopyMode fromShown, bool logChange = true, int manualWidth = 0, int fromWidth = 0)
    {
        var width = mode == HeaderCopyMode.FilesN ? AdaptiveCopyPolicy.ClampManualFiles(manualWidth) : 0;
        _pendingHeaderCopy = mode;
        _pendingHeaderFrom = fromShown;
        _pendingManualWidth = width;
        _pendingManualFromWidth = fromWidth;
        var chosen = mode != HeaderCopyMode.FollowSaved;
        var detail = mode == HeaderCopyMode.FollowSaved ? null : HeaderCopy.SwitchDetail(fromShown, mode, width, fromWidth);
        foreach (var running in _running.Values)
        {
            var changed = running.Job.HeaderCopyMode != mode
                || (mode == HeaderCopyMode.FilesN && running.Job.ManualFileWidth != width);
            running.Job.HeaderCopyMode = mode;
            running.Job.HeaderCopyModeChosen = chosen;
            if (mode == HeaderCopyMode.FilesN)
            {
                running.Job.ManualFileWidth = width;
            }

            running.Job.HeaderCopySwitchDetail = detail;
            if (logChange && changed && mode != HeaderCopyMode.FollowSaved)
            {
                var jobName = string.IsNullOrWhiteSpace(running.Job.Name) ? running.Job.Id[..8] : running.Job.Name;
                Log.Info(running.Job.Id, jobName, HeaderCopy.SwitchLine(fromShown, mode, width, fromWidth));
            }
        }
    }

    public event EventHandler<JobProgress>? ProgressChanged;
    public event EventHandler? QueueChanged;
    public event EventHandler? HistoryChanged;
    public event EventHandler<CompareActivity>? CompareActivity;

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

    public bool HasRunningCompare => !_compares.IsEmpty;

    public bool HasBackgroundRundown => !_rundowns.IsEmpty;

    /// <summary>True while a copy is running. Rundown in the background does not block Start.</summary>
    public bool BlocksStart =>
        _running.Values.Any(r => !r.Cts.IsCancellationRequested);

    public Job? TryGetRunningJob(string? jobId = null)
    {
        if (jobId is not null)
        {
            if (_running.TryGetValue(jobId, out var named))
            {
                return named.Job;
            }

            if (_compares.TryGetValue(jobId, out var compare))
            {
                return compare.Job;
            }
        }

        return _running.Values.Select(r => r.Job).FirstOrDefault();
    }

    public Job? TryGetRunningCompare() =>
        _compares.Values.Select(r => r.Job).FirstOrDefault();

    private bool TryGetActive(string jobId, out Running running)
    {
        if (_running.TryGetValue(jobId, out running!))
        {
            return true;
        }

        if (_compares.TryGetValue(jobId, out running!))
        {
            return true;
        }

        running = null!;
        return false;
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
            if (_running.ContainsKey(job.Id) || _compares.ContainsKey(job.Id) || _rundowns.ContainsKey(job.Id))
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
                if (job.Kind == JobKind.Compare)
                {
                    _forceCompareId = job.Id;
                }
                else
                {
                    _forceStartId = job.Id;
                }
            }

            SaveQueue();
        }

        RaiseQueueChanged();
        if (startNow)
        {
            if (job.Kind == JobKind.Compare)
            {
                KickCompare();
            }
            else
            {
                Kick();
            }
        }
    }

    public void Remove(string jobId)
    {
        if (_running.ContainsKey(jobId) || _compares.ContainsKey(jobId) || _rundowns.ContainsKey(jobId))
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
        if (_compares.ContainsKey(jobId) || _running.ContainsKey(jobId))
        {
            ResumePaused(jobId);
            return;
        }

        if (_rundowns.TryGetValue(jobId, out var rundown))
        {
            rundown.Cts.Cancel();
        }

        _progress.TryRemove(jobId, out _);

        var compare = false;
        lock (_queueLock)
        {
            var job = _queue.FirstOrDefault(j => j.Id == jobId);
            if (job is null)
            {
                return;
            }

            compare = job.Kind == JobKind.Compare;
            if (!compare && _running.IsEmpty && GlobalPause.IsPaused)
            {
                GlobalPause.Resume();
            }

            job.OnHold = false;
            if (job.Status is JobStatus.Cancelled or JobStatus.Incomplete or JobStatus.Failed
                or JobStatus.Paused or JobStatus.PausedOutsideHours)
            {
                job.Status = JobStatus.Pending;
            }

            if (job.Status == JobStatus.Pending)
            {
                if (compare)
                {
                    _forceCompareId = job.Id;
                }
                else
                {
                    _forceStartId = job.Id;
                }
            }

            SaveQueue();
        }

        RaiseQueueChanged();
        if (compare)
        {
            KickCompare(drain: true);
        }
        else
        {
            Kick(drain: true);
        }
    }

    public void SetHold(string jobId, bool hold)
    {
        var compare = false;
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

            compare = job.Kind == JobKind.Compare;
            job.OnHold = hold;
            if (hold && _forceStartId == jobId)
            {
                _forceStartId = null;
            }

            if (hold && _forceCompareId == jobId)
            {
                _forceCompareId = null;
            }

            SaveQueue();
        }

        RaiseQueueChanged();
        if (!hold)
        {
            if (compare)
            {
                KickCompare();
            }
            else
            {
                Kick();
            }
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
        if (TryGetActive(job.Id, out var running))
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

        Interlocked.Exchange(ref _kickAgain, 1);
        if (Interlocked.CompareExchange(ref _pumping, 1, 0) != 0)
        {
            return;
        }

        _ = PumpAsync();
    }

    /// <summary>Start the compare lane. A running transfer does not block it, and it does not take the copy slot.</summary>
    public void KickCompare(bool drain = false)
    {
        if (drain)
        {
            Interlocked.Exchange(ref _compareDrain, 1);
        }

        Interlocked.Exchange(ref _compareKickAgain, 1);
        if (Interlocked.CompareExchange(ref _comparePumping, 1, 0) != 0)
        {
            return;
        }

        _ = PumpCompareAsync();
    }

    public async Task StartAsync(Job job, bool resumeJournal, CancellationToken cancellationToken)
    {
        await RunCopyCoreAsync(job, resumeJournal, cancellationToken).ConfigureAwait(false);
        await WaitForRundownAsync(job.Id).ConfigureAwait(false);
    }

    private async Task RunCopyCoreAsync(Job job, bool resumeJournal, CancellationToken cancellationToken)
    {
        var compare = job.Kind == JobKind.Compare;
        var lane = compare ? _compares : _running;
        if (compare)
        {
            if (!_compares.IsEmpty)
            {
                throw new InvalidOperationException("A compare is already running.");
            }
        }
        else if (_running.Count >= MaxConcurrentJobs)
        {
            throw new InvalidOperationException(
                $"v1 runs one copy at a time ({MaxConcurrentJobs} concurrent). Stop the current copy or wait for it to finish.");
        }

        EnsureName(job);

        JobSources.RemapVolumes(job);
        if (job.SourcePaths is not { Count: > 0 } && !string.IsNullOrWhiteSpace(job.SourcePath))
        {
            job.SourcePaths = [job.SourcePath];
        }

        JobDestinations.EnsureList(job);

        PackPolicy.ApplySettings(job.Options, AppSettingsStore.Load(_paths));

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

        if (!compare)
        {
            AppSettingsStore.SaveLastJobId(_paths, job.Id);
        }

        var pause = new PauseGate { Parent = compare ? CompareLanePause : GlobalPause };
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new Running(job, journal, pause, cts);
        if (!lane.TryAdd(job.Id, running))
        {
            journal.Dispose();
            throw new InvalidOperationException("That job is already running.");
        }

        if (job.Kind != JobKind.Compare && _pendingHeaderCopy != HeaderCopyMode.FollowSaved)
        {
            job.HeaderCopyMode = _pendingHeaderCopy;
            job.HeaderCopyModeChosen = true;
            if (_pendingHeaderCopy == HeaderCopyMode.FilesN)
            {
                job.ManualFileWidth = _pendingManualWidth;
            }

            job.HeaderCopySwitchDetail = HeaderCopy.SwitchDetail(_pendingHeaderFrom, _pendingHeaderCopy, _pendingManualWidth, _pendingManualFromWidth);
            var jobName = string.IsNullOrWhiteSpace(job.Name) ? job.Id[..8] : job.Name;
            Log.Info(job.Id, jobName, HeaderCopy.SwitchLine(_pendingHeaderFrom, _pendingHeaderCopy, _pendingManualWidth, _pendingManualFromWidth));
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
        var compareBeat = new CompareBeat();
        try
        {
            Log.Info(job.Id, job.Name, $"Job started. Log: {_paths.JobLogFile(job.Id)}");
            if (job.Kind == JobKind.Compare)
            {
                await Task.Run(() => RunCompare(job, journal, pause, progress, cts.Token, compareBeat)).ConfigureAwait(false);
            }
            else
            {
                await _engine.RunAsync(job, journal, Budget, pause, Log, progress, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.ResultMessage = job.Kind == JobKind.Compare
                ? "Compare stopped. Resume continues the saved compare."
                : "Stopped. Progress is saved; you can Resume.";
            Log.Info(job.Id, job.Name, job.ResultMessage);
            reportFinal = true;
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.ResultMessage = ProgressHeader.IsExceptionDump(ex.Message) || ex is NullReferenceException or ObjectDisposedException
                ? "Internal error while copying. Progress is saved; you can Resume. See the Console log."
                : ex.Message;
            Log.Error(job.Id, job.Name, ex.ToString());
            reportFinal = true;
        }
        finally
        {
            if (lane.TryRemove(job.Id, out var finished))
            {
                keepHeartbeat = finished.KeepHeartbeatOnCancel;
            }

            try
            {
                if (job.Kind == JobKind.Compare)
                {
                    var compareTerminal = job.Status is JobStatus.Completed or JobStatus.Incomplete or JobStatus.Failed
                        || (job.Status == JobStatus.Cancelled && !keepHeartbeat);
                    if (compareTerminal || (compareBeat.Percent < 0.05 && string.IsNullOrWhiteSpace(compareBeat.File)))
                    {
                        JobHeartbeat.Clear(journal);
                    }
                    else
                    {
                        JobHeartbeat.Write(journal, job, compareBeat.Percent, compareBeat.File);
                    }
                }
                else
                {
                    var hb = SafeTotals(journal);
                    var percent = hb.Bytes > 0 ? 100.0 * hb.DoneBytes / hb.Bytes : 0;
                    var terminal = job.Status is JobStatus.Completed or JobStatus.Incomplete or JobStatus.Failed
                        || (job.Status == JobStatus.Cancelled && !keepHeartbeat);
                    if (terminal || (!JobHeartbeat.HasCopyProgress(percent, null, job) && hb.DoneFiles == 0))
                    {
                        JobHeartbeat.Clear(journal);
                    }
                    else
                    {
                        JobHeartbeat.Write(journal, job, percent, null);
                    }
                }
            }
            catch
            {
                // heartbeat must not hide the real result
            }

            cts.Dispose();
            if (job.Kind == JobKind.Compare)
            {
                try
                {
                    if (job.Status is JobStatus.Completed or JobStatus.Incomplete or JobStatus.Cancelled or JobStatus.Failed)
                    {
                        HistoryStore.Record(_paths, job);
                        HistoryChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
                catch
                {
                    // history is best-effort
                }

                try
                {
                    journal.Dispose();
                }
                catch
                {
                    // journal already closed
                }
            }
            else
            {
                BeginPostCopy(job, journal, progress);
                rundownOwnsJournal = true;
            }
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

        if (!compare)
        {
            job.HeaderCopyMode = HeaderCopyMode.FollowSaved;
            job.HeaderCopyModeChosen = false;
            job.HeaderCopySwitchDetail = null;
            job.ManualFileWidth = 0;
            _pendingHeaderCopy = HeaderCopyMode.FollowSaved;
            _pendingHeaderFrom = HeaderCopyMode.Adaptive;
            _pendingManualWidth = 0;
            _pendingManualFromWidth = 0;
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
        if (TryGetActive(jobId, out var running))
        {
            running.Pause.Pause();
            running.Job.Status = JobStatus.Paused;
            TransferRundown.MarkPaused(running.Job);
            running.Journal.SaveJob(running.Job);
            Log.Info(jobId, running.Job.Name, "Paused.");
            ReportFinal(running.Job);
        }
    }

    public void RequestPauseAfterFile(string jobId)
    {
        if (TryGetActive(jobId, out var running))
        {
            running.Pause.RequestPauseAfterFile();
            Log.Info(jobId, running.Job.Name, "Pause after this file requested.");
        }
    }

    public void CancelPauseAfterFile(string jobId)
    {
        if (TryGetActive(jobId, out var running) && running.Pause.PauseAfterFileRequested)
        {
            running.Pause.CancelPauseAfterFile();
            Log.Info(jobId, running.Job.Name, "Pause after this file cancelled.");
        }
    }

    public bool IsPauseAfterFilePending(string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId) || !TryGetActive(jobId, out var running))
        {
            return false;
        }

        return running.Pause.PauseAfterFileRequested;
    }

    public TimeSpan? EstimateCurrentFileEta(string jobId)
    {
        if (!TryGetActive(jobId, out var running))
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
        TryGetActive(jobId, out var running)
        && running.Job.Kind != JobKind.Compare
        && running.Job.Status == JobStatus.Copying
        && !running.Pause.IsEffectivelyPaused;

    public void PauseAll()
    {
        GlobalPause.Pause();
        CompareLanePause.Pause();
        foreach (var running in _running.Values.Concat(_compares.Values))
        {
            running.Job.Status = JobStatus.Paused;
            TransferRundown.MarkPaused(running.Job);
            running.Journal.SaveJob(running.Job);
            Log.Info(running.Job.Id, running.Job.Name, "Paused (all jobs).");
            ReportFinal(running.Job);
        }
    }

    public void ResumeAll()
    {
        GlobalPause.Resume();
        CompareLanePause.Resume();
        foreach (var running in _running.Values.Concat(_compares.Values))
        {
            if (running.Pause.IsPaused)
            {
                continue;
            }

            running.Job.Status = JobStatus.Copying;
            TransferRundown.MarkUnpaused(running.Job);
            running.Journal.SaveJob(running.Job);
            Log.Info(running.Job.Id, running.Job.Name, "Resumed (all jobs).");
            ReportFinal(running.Job);
        }

        Kick(drain: true);
    }

    public void ResumePaused(string jobId)
    {
        var compareLane = _compares.TryGetValue(jobId, out var running);
        if (!compareLane && !_running.TryGetValue(jobId, out running))
        {
            return;
        }

        if (running is null)
        {
            return;
        }

        if (!compareLane && GlobalPause.IsPaused)
        {
            foreach (var other in _running.Values)
            {
                if (other.Job.Id == jobId)
                {
                    continue;
                }

                other.Pause.Pause();
                other.Job.Status = JobStatus.Paused;
                TransferRundown.MarkPaused(other.Job);
                other.Journal.SaveJob(other.Job);
            }

            GlobalPause.Resume();
        }

        if (compareLane && CompareLanePause.IsPaused)
        {
            CompareLanePause.Resume();
        }

        running.Job.Status = JobStatus.Copying;
        TransferRundown.MarkUnpaused(running.Job);
        running.Journal.SaveJob(running.Job);
        running.Pause.Resume();
        Log.Info(jobId, running.Job.Name, "Resumed.");
        ReportFinal(running.Job);
    }

    public void Stop(string jobId, bool clearHeartbeat = true)
    {
        if (_compares.TryGetValue(jobId, out var compareRun))
        {
            compareRun.KeepHeartbeatOnCancel = !clearHeartbeat;
            compareRun.Pause.Resume();
            if (_compares.Count <= 1)
            {
                CompareLanePause.Resume();
            }

            compareRun.Cts.Cancel();
            if (compareRun.Job.Status is not JobStatus.Completed and not JobStatus.Failed
                and not JobStatus.Incomplete and not JobStatus.Cancelled)
            {
                compareRun.Job.Status = JobStatus.Cancelled;
                compareRun.Job.ResultMessage = "Stopping…";
            }

            ReportFinal(compareRun.Job);
            return;
        }

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

        foreach (var id in _compares.Keys.ToList())
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
        if (!string.IsNullOrEmpty(jobId) && TryGetActive(jobId, out var running))
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

    /// <summary>Clear the launch-resume dirty flag. Journal stays for Resume last.</summary>
    public void ClearDirtyHeartbeat(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return;
        }

        var dir = _paths.JobDirectory(jobId);
        JobJournal? journal = null;
        try
        {
            if (JobJournal.Exists(dir))
            {
                journal = JobJournal.Open(dir);
            }
        }
        catch
        {
            // sidecar still has to go even if job.db is locked by another instance
        }

        try
        {
            if (journal is not null)
            {
                JobHeartbeat.Clear(journal);
            }
            else
            {
                JobHeartbeat.ClearSidecar(dir, jobId);
            }
        }
        catch
        {
            JobHeartbeat.ClearSidecar(dir, jobId);
        }
        finally
        {
            journal?.Dispose();
        }
    }

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

        jobs = jobs.Where(j => j.Kind != JobKind.Compare).ToList();
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
            var only = _progress.Values.First();
            if (jobs.Any(j => j.Id == only.JobId))
            {
                return only;
            }
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
                var postCopy = p.IsBackgroundStage || job.Status == JobStatus.Verifying;
                if (postCopy)
                {
                    var copied = p.BytesCopied > 0 ? p.BytesCopied : job.BytesCopied;
                    bytes += copied;
                    total += copied;
                    var doneFiles = job.SourceFiles > 0 ? job.SourceFiles : Math.Max(p.FilesTotal, p.FilesCopied);
                    files += doneFiles;
                    filesTotal += doneFiles;
                }
                else
                {
                    OverallProgress.AddBytes(job, p, ref bytes, ref total);
                    files += p.FilesCopied;
                    filesTotal += p.FilesTotal;
                    speed += p.BytesPerSecond;
                    live |= p.Status is JobStatus.Preparing or JobStatus.Copying or JobStatus.Enumerating
                        or JobStatus.Paused or JobStatus.PausedOutsideHours;
                }

                issues += p.IssueCount;
                if (p.StartedUtc is { } s && (started is null || s < started))
                {
                    started = s;
                }
            }
            else
            {
                OverallProgress.AddBytes(job, null, ref bytes, ref total);
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
                Interlocked.Exchange(ref _kickAgain, 0);
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
                        if (forced is null || forced.OnHold || forced.Status != JobStatus.Pending || forced.Kind == JobKind.Compare)
                        {
                            _forceStartId = null;
                        }
                    }

                    var forceId = _forceStartId;
                    next = JobDue.FindNext(
                        _queue.Where(j => j.Kind != JobKind.Compare),
                        DateTimeOffset.Now,
                        forceId,
                        includeUnscheduled: allowUnscheduled || forceId is not null);
                    if (next is not null && forceId == next.Id)
                    {
                        _forceStartId = null;
                    }

                    if (next is null)
                    {
                        if (Volatile.Read(ref _kickAgain) == 1)
                        {
                            continue;
                        }

                        break;
                    }
                }

                var resume = JobJournal.Exists(_paths.JobDirectory(next.Id));
                if (_rundowns.ContainsKey(next.Id))
                {
                    if (_rundowns.TryGetValue(next.Id, out var leftover))
                    {
                        leftover.Cts.Cancel();
                    }

                    await WaitForRundownAsync(next.Id).ConfigureAwait(false);
                }

                try
                {
                    await RunCopyCoreAsync(next, resume, _lifetime.Token).ConfigureAwait(false);
                    allowUnscheduled = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    next.Status = JobStatus.Failed;
                    next.ResultMessage = ProgressHeader.IsExceptionDump(ex.Message) || ex is NullReferenceException or ObjectDisposedException
                        ? "Internal error while copying. Progress is saved; you can Resume. See the Console log."
                        : ex.Message;
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
            if (Interlocked.CompareExchange(ref _kickAgain, 0, 1) == 1)
            {
                Kick(drain: allowUnscheduled);
            }
            else
            {
                var again = false;
                lock (_queueLock)
                {
                    again = !_lifetime.IsCancellationRequested
                            && _running.Count < MaxConcurrentJobs
                            && !GlobalPause.IsPaused
                            && JobDue.FindNext(
                                _queue.Where(j => j.Kind != JobKind.Compare),
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
    }

    private async Task PumpCompareAsync()
    {
        var allowUnscheduled = Interlocked.Exchange(ref _compareDrain, 0) == 1;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _compareKickAgain, 0);
                Job? next;
                lock (_queueLock)
                {
                    if (!_compares.IsEmpty || CompareLanePause.IsPaused)
                    {
                        break;
                    }

                    if (_forceCompareId is not null)
                    {
                        var forced = _queue.FirstOrDefault(j => j.Id == _forceCompareId);
                        if (forced is null || forced.OnHold || forced.Status != JobStatus.Pending || forced.Kind != JobKind.Compare)
                        {
                            _forceCompareId = null;
                        }
                    }

                    var forceId = _forceCompareId;
                    next = JobDue.FindNext(
                        _queue.Where(j => j.Kind == JobKind.Compare),
                        DateTimeOffset.Now,
                        forceId,
                        includeUnscheduled: allowUnscheduled || forceId is not null);
                    if (next is not null && forceId == next.Id)
                    {
                        _forceCompareId = null;
                    }

                    if (next is null)
                    {
                        if (Volatile.Read(ref _compareKickAgain) == 1)
                        {
                            continue;
                        }

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
            Interlocked.Exchange(ref _comparePumping, 0);
            if (Interlocked.CompareExchange(ref _compareKickAgain, 0, 1) == 1)
            {
                KickCompare(drain: allowUnscheduled);
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

    private void RunCompare(
        Job job,
        JobJournal journal,
        PauseGate pause,
        IProgress<JobProgress> progress,
        CancellationToken cancellationToken,
        CompareBeat beat)
    {
        var advanced = job.Options.CompareAdvanced;
        var hashDest = advanced && job.Options.CompareHash;
        var hashSource = job.Options.HashSourceForCompare || hashDest;
        var manifestPath = hashSource ? CompareManifestStore.JobFile(journal.Directory) : null;
        var options = new DirectoryCompareOptions
        {
            Advanced = advanced,
            Hash = hashDest,
            HashSource = job.Options.HashSourceForCompare,
            FatTimestampTolerance = job.Options.FatTimestampTolerance
        };
        job.Status = JobStatus.Enumerating;
        journal.SaveJob(job);
        var lastBeat = DateTime.UtcNow;
        ReportCompare(job, progress, JobStatus.Enumerating, "Comparing…", null);

        var result = DirectoryComparer.Compare(
            job.SourcePath,
            job.DestinationPath,
            options,
            cancellationToken,
            pause,
            snapshot =>
            {
                var hashing = snapshot.Pace is not null;
                var status = pause.IsEffectivelyPaused
                    ? JobStatus.Paused
                    : hashing ? JobStatus.Copying : JobStatus.Enumerating;
                if (job.Status is not (JobStatus.Cancelled or JobStatus.Failed or JobStatus.Completed))
                {
                    job.Status = status;
                }

                double percent = 0;
                if (snapshot.Pace is { BytesTotal: > 0 } pace)
                {
                    percent = 100.0 * pace.BytesDone / pace.BytesTotal;
                }
                else if (!string.IsNullOrWhiteSpace(snapshot.CurrentRelative))
                {
                    percent = 0.05;
                }

                beat.Percent = percent;
                beat.File = snapshot.CurrentRelative;
                if (DateTime.UtcNow - lastBeat >= TimeSpan.FromSeconds(2))
                {
                    try
                    {
                        JobHeartbeat.Write(journal, job, percent, snapshot.CurrentRelative);
                    }
                    catch
                    {
                        // a missed pulse still leaves the manifest
                    }

                    lastBeat = DateTime.UtcNow;
                }

                ReportCompare(job, progress, job.Status, "Comparing", snapshot);
                CompareActivity?.Invoke(this, new CompareActivity
                {
                    JobId = job.Id,
                    Job = job,
                    Progress = snapshot
                });
            },
            manifestPath,
            _paths.Jobs,
            message => Log.Info(job.Id, job.Name, message));

        if (result.Completed)
        {
            if (result.Hashed && manifestPath is not null)
            {
                CompareManifestStore.Delete(manifestPath);
            }

            job.Status = JobStatus.Completed;
            job.EndedUtc = DateTimeOffset.UtcNow;
            job.ResultMessage = result.Hashed
                ? "Compare finished (hashed)."
                : result.Advanced ? "Advanced compare finished." : "Compare finished.";
            job.SourceFiles = result.LeftFiles;
            job.DestFiles = result.RightFiles;
            job.SourceFolders = result.LeftFolders;
            job.DestFolders = result.RightFolders;
            beat.Percent = 100;
            beat.File = null;
        }
        else if (result.Canceled)
        {
            job.Status = JobStatus.Cancelled;
            job.EndedUtc = DateTimeOffset.UtcNow;
            job.ResultMessage = "Compare stopped. Resume continues the saved compare.";
        }
        else
        {
            job.Status = JobStatus.Failed;
            job.EndedUtc = DateTimeOffset.UtcNow;
            job.ResultMessage = string.IsNullOrWhiteSpace(result.Error) ? "Compare failed." : result.Error;
        }

        journal.SaveJob(job);
        Log.Info(job.Id, job.Name, job.ResultMessage ?? job.Status.ToString());
        ReportCompare(job, progress, job.Status, job.ResultMessage ?? "Compare", null);
        CompareActivity?.Invoke(this, new CompareActivity
        {
            JobId = job.Id,
            Job = job,
            Result = result
        });
    }

    private void ReportCompare(Job job, IProgress<JobProgress> progress, JobStatus status, string message, DirectoryCompareProgress? snapshot)
    {
        var pace = snapshot?.Pace;
        progress.Report(new JobProgress
        {
            JobId = job.Id,
            JobName = job.Name,
            Status = status,
            Message = message,
            CurrentFile = snapshot?.CurrentRelative,
            CurrentFileBytesCopied = pace?.FileBytesDone ?? 0,
            CurrentFileBytesTotal = pace?.FileBytesTotal ?? 0,
            BytesCopied = pace?.BytesDone ?? 0,
            BytesTotal = pace?.BytesTotal ?? 0,
            FilesCopied = pace?.FilesDone ?? 0,
            FilesTotal = pace?.FilesTotal ?? 0,
            BytesPerSecond = pace?.BytesPerSecond ?? 0,
            SpeedMeasured = pace is { BytesPerSecond: > 0 },
            Eta = snapshot?.Stage?.Remaining ?? pace?.OverallEta,
            StageIndex = snapshot?.Stage?.Index ?? 1,
            StageCount = snapshot?.Stage?.Count ?? 1,
            StageName = snapshot?.Stage?.Name ?? "Compare",
            StartedUtc = job.StartedUtc,
            PausedUtc = status is JobStatus.Paused or JobStatus.PausedOutsideHours ? job.PausedUtc ?? DateTimeOffset.UtcNow : job.PausedUtc,
            StageStartedUtc = snapshot?.Stage is { } stage
                ? DateTimeOffset.UtcNow - stage.Elapsed
                : job.StartedUtc
        });
    }

    private static void EnsureName(Job job)
    {
        if (!string.IsNullOrWhiteSpace(job.Name))
        {
            return;
        }

        if (job.Kind == JobKind.Compare)
        {
            var leaf = Path.GetFileName(job.SourcePath.TrimEnd('\\', '/'));
            job.Name = string.IsNullOrWhiteSpace(leaf) ? "Compare" : "Compare " + leaf;
            return;
        }

        job.Name = JobSources.DefaultName(job);
        if (string.IsNullOrWhiteSpace(job.Name))
        {
            job.Name = Path.GetFileName(job.SourcePath.TrimEnd('\\', '/'));
        }
        if (string.IsNullOrWhiteSpace(job.Name))
        {
            job.Name = job.Id[..8];
        }
    }

    private void ReportFinal(Job job)
    {
        var last = GetProgress(job.Id);
        var finished = job.Status is JobStatus.Completed or JobStatus.Incomplete or JobStatus.Cancelled or JobStatus.Failed;
        var p = new JobProgress
        {
            JobId = job.Id,
            JobName = job.Name,
            Status = job.Status,
            Message = ProgressHeader.HeaderResult(job),
            IssueCount = job.IssueCount,
            StartedUtc = job.StartedUtc,
            EndedUtc = job.EndedUtc,
            CurrentFile = finished ? null : last?.CurrentFile,
            CurrentFileBytesCopied = finished ? 0 : last?.CurrentFileBytesCopied ?? 0,
            CurrentFileBytesTotal = finished ? 0 : last?.CurrentFileBytesTotal ?? 0,
            CurrentFileStartedUtc = finished ? null : last?.CurrentFileStartedUtc,
            BytesCopied = last?.BytesCopied > 0 ? last.BytesCopied : job.BytesCopied,
            BytesTotal = last?.BytesTotal ?? 0,
            FilesCopied = last?.FilesCopied > 0 ? last.FilesCopied : job.DestFiles,
            FilesTotal = last?.FilesTotal > 0 ? last.FilesTotal : job.SourceFiles,
            StageIndex = finished ? 0 : last?.StageIndex ?? 0,
            StageCount = finished ? 0 : last?.StageCount ?? 0,
            StageName = finished ? null : last?.StageName,
            StageStartedUtc = finished ? null : last?.StageStartedUtc,
            TypeSummary = last?.TypeSummary,
            BytesPerSecond = finished ? 0 : last?.BytesPerSecond ?? 0
        };
        _progress[job.Id] = p;
        ProgressChanged?.Invoke(this, p);
    }

    private void PruneFinishedProgress()
    {
        foreach (var key in _progress.Keys.ToList())
        {
            if (!_running.ContainsKey(key) && !_compares.ContainsKey(key) && !_rundowns.ContainsKey(key))
            {
                _progress.TryRemove(key, out _);
            }
        }
    }

    private void FillProgress(Job job, JobJournal journal, JobProgress p)
    {
        var rundown = p.IsBackgroundStage;
        var totals = SafeTotals(journal);
        var copied = Math.Max(p.BytesCopied, totals.DoneBytes);
        var total = Math.Max(p.BytesTotal, totals.Bytes);
        var started = p.StartedUtc ?? job.StartedUtc;
        var elapsed = started is { } startAt ? DateTimeOffset.UtcNow - startAt : TimeSpan.Zero;
        var rate = rundown
            ? 0
            : p.SpeedMeasured
                ? p.BytesPerSecond
                : ByteFormatter.EffectiveRate(p.BytesPerSecond, copied, elapsed);
        var filled = new JobProgress
        {
            JobId = p.JobId,
            JobName = p.JobName,
            Status = p.Status,
            CurrentFile = p.CurrentFile,
            CurrentFileBytesCopied = p.CurrentFileBytesCopied,
            CurrentFileBytesTotal = p.CurrentFileBytesTotal,
            CurrentFileStartedUtc = p.CurrentFileStartedUtc,
            Message = p.Message,
            CloudDestination = p.CloudDestination,
            BytesCopied = copied,
            BytesTotal = total,
            FilesCopied = rundown ? p.FilesCopied : Math.Max(p.FilesCopied, totals.DoneFiles),
            FilesTotal = rundown ? p.FilesTotal : Math.Max(p.FilesTotal, totals.Files),
            IssueCount = Math.Max(p.IssueCount, totals.Failed),
            BytesPerSecond = rate,
            SpeedMeasured = p.SpeedMeasured,
            Eta = rundown ? p.Eta : EstimateEta(copied, total, rate),
            StageIndex = p.StageIndex,
            StageCount = p.StageCount,
            StageName = p.StageName,
            StartedUtc = started,
            PausedUtc = p.PausedUtc ?? job.PausedUtc,
            StageStartedUtc = p.StageStartedUtc,
            TypeSummary = p.TypeSummary,
            EndedUtc = job.EndedUtc ?? p.EndedUtc,
            RundownDone = p.RundownDone,
            RundownTotal = p.RundownTotal,
            RundownPerSecond = p.RundownPerSecond
        };
        _progress[job.Id] = filled;
        ProgressChanged?.Invoke(this, filled);
    }

    private void BeginPostCopy(Job job, JobJournal journal, IProgress<JobProgress> progress)
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
        work.Task = Task.Run(() => ExecutePostCopy(work));
        _rundowns[job.Id] = work;
    }

    private void ExecutePostCopy(RundownWork work)
    {
        var job = work.Job;
        var name = string.IsNullOrWhiteSpace(job.Name) ? job.Id[..8] : job.Name;
        var token = work.Cts.Token;
        try
        {
            if (ShouldVerify(job))
            {
                job.Status = JobStatus.Verifying;
                work.Journal.SaveJob(job);
                lock (_queueLock)
                {
                    SaveQueue();
                }

                RaiseQueueChanged();
                _engine.Verify(job, work.Journal, Log, work.Progress, token);
                work.Journal.SaveJob(job);
            }
        }
        catch (OperationCanceledException)
        {
            if (job.Status is not JobStatus.Completed and not JobStatus.Failed
                and not JobStatus.Incomplete and not JobStatus.Cancelled)
            {
                job.Status = JobStatus.Cancelled;
                job.ResultMessage = "Stopped during verify.";
            }
        }
        catch (Exception ex)
        {
            Log.Error(job.Id, name, ex.ToString());
            job.Status = JobStatus.Incomplete;
            job.ResultMessage = "Verify could not finish. Copy progress is kept; details are in the Console log.";
        }

        ExecuteRundown(work);
    }

    private static bool ShouldVerify(Job job) =>
        job.Options?.DryRun != true
        && job.Status is not JobStatus.Failed and not JobStatus.Cancelled and not JobStatus.Completed;

    private void ExecuteRundown(RundownWork work)
    {
        var job = work.Job;
        var name = string.IsNullOrWhiteSpace(job.Name) ? job.Id[..8] : job.Name;
        var token = work.Cts.Token;
        try
        {
            Log.Info(job.Id, name, "Rundown running in background");
            job.Options ??= new JobOptions();
            ReportRundown(work, new RundownProgress(0, Math.Max(1, job.SourceFiles), 0, null, "destination"));
            CopyMapping? mapping = null;
            try
            {
                mapping = JobSources.Resolve(job, job.DestinationPath, job.Options.IncludeSourceFolderName).FirstOrDefault();
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
            if (ex is NullReferenceException or ObjectDisposedException &&
                (string.IsNullOrWhiteSpace(job.ResultMessage)
                 || job.ResultMessage.Contains("Object reference not set", StringComparison.OrdinalIgnoreCase)))
            {
                job.ResultMessage =
                    "Rundown could not finish counting files. Copy progress is kept; details are in the Console log.";
            }
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

    private sealed class CompareBeat
    {
        public double Percent;
        public string? File;
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

public sealed class CompareActivity
{
    public string JobId { get; init; } = "";
    public Job? Job { get; init; }
    public DirectoryCompareProgress? Progress { get; init; }
    public DirectoryCompareResult? Result { get; init; }
}
