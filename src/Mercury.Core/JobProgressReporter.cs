namespace Mercury;

public sealed class JobProgressReporter : IProgress<JobProgress>
{
    private readonly Job _job;
    private readonly string _name;
    private readonly bool _cloud;
    private readonly IReadOnlyList<CopyStage> _stages;
    private readonly IProgress<JobProgress>? _progress;
    private readonly IJobLog? _log;
    private readonly object _lock = new();

    private CopyStageKind _kind = CopyStageKind.PreparingDestination;
    private int _index;
    private DateTimeOffset _stageStarted = DateTimeOffset.UtcNow;
    private string? _message;
    private string? _current;
    private SpeedTracker? _speed;
    private double _reportedBps;
    private int _filesCopied;
    private int _filesTotal;
    private long _bytesCopied;
    private long _bytesTotal;

    public JobProgressReporter(
        Job job,
        string name,
        bool cloud,
        IReadOnlyList<CopyStage> stages,
        IProgress<JobProgress>? progress,
        IJobLog? log)
    {
        _job = job;
        _name = name;
        _cloud = cloud;
        _stages = stages;
        _progress = progress;
        _log = log;
    }

    public void Enter(CopyStageKind kind, JobStatus status, string message)
    {
        lock (_lock)
        {
            _kind = kind;
            _index = CopyPipeline.IndexOf(_stages, kind);
            _stageStarted = DateTimeOffset.UtcNow;
            _message = message;
            _current = null;
            _job.Status = status;
        }

        var label = CopyPipeline.LabelOf(_stages, kind);
        var index = CopyPipeline.IndexOf(_stages, kind);
        _log?.Info(_job.Id, _name, CopyPipeline.Format(index, _stages.Count, label));
        Push();
    }

    public void Update(
        string? message = null,
        string? current = null,
        SpeedTracker? speed = null,
        int? filesCopied = null,
        int? filesTotal = null,
        long? bytesCopied = null,
        long? bytesTotal = null)
    {
        lock (_lock)
        {
            if (message is not null)
            {
                _message = message;
            }

            if (current is not null)
            {
                _current = current;
            }

            if (speed is not null)
            {
                _speed = speed;
            }

            if (filesCopied is not null)
            {
                _filesCopied = filesCopied.Value;
            }

            if (filesTotal is not null)
            {
                _filesTotal = filesTotal.Value;
            }

            if (bytesCopied is not null)
            {
                _bytesCopied = bytesCopied.Value;
            }

            if (bytesTotal is not null)
            {
                _bytesTotal = bytesTotal.Value;
            }
        }

        Push();
    }

    public void Pulse() => Push();

    public async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Pulse();
            }
        }
        catch (OperationCanceledException)
        {
            // run finished
        }
    }

    void IProgress<JobProgress>.Report(JobProgress value)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(value.Message))
            {
                _message = value.Message;
            }

            _current = value.CurrentFile ?? _current;
            if (value.BytesCopied > 0)
            {
                _bytesCopied = value.BytesCopied;
            }

            if (value.BytesPerSecond > 0)
            {
                _reportedBps = value.BytesPerSecond;
            }

            if (value.FilesCopied > 0)
            {
                _filesCopied = value.FilesCopied;
            }

            if (value.FilesTotal > 0)
            {
                _filesTotal = value.FilesTotal;
            }

            if (value.BytesTotal > 0)
            {
                _bytesTotal = value.BytesTotal;
            }

            if (value.Status != JobStatus.Pending)
            {
                _job.Status = value.Status;
            }
        }

        Push();
    }

    private void Push()
    {
        if (_progress is null)
        {
            return;
        }

        JobProgress snapshot;
        lock (_lock)
        {
            var speed = _speed;
            var bytesCopied = speed?.Bytes > 0 ? speed.Bytes : _bytesCopied;
            var bps = speed?.EffectiveBytesPerSecond ?? 0;
            if (bps < 1 && _reportedBps >= 1)
            {
                bps = _reportedBps;
            }

            if (bps < 1)
            {
                var elapsed = _job.StartedUtc is { } start
                    ? DateTimeOffset.UtcNow - start
                    : TimeSpan.Zero;
                bps = ByteFormatter.EffectiveRate(0, bytesCopied, elapsed);
            }

            snapshot = new JobProgress
            {
                JobId = _job.Id,
                JobName = _name,
                Status = _job.Status,
                CurrentFile = _current,
                Message = _message,
                CloudDestination = _cloud,
                BytesCopied = bytesCopied,
                BytesTotal = _bytesTotal,
                FilesCopied = _filesCopied,
                FilesTotal = _filesTotal,
                BytesPerSecond = bps,
                IssueCount = _job.IssueCount,
                StageIndex = _index,
                StageCount = _stages.Count,
                StageName = CopyPipeline.LabelOf(_stages, _kind),
                StartedUtc = _job.StartedUtc,
                StageStartedUtc = _stageStarted
            };
        }

        _progress.Report(snapshot);
    }
}
