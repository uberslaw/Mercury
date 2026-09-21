using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Mercury;

public sealed class QueueJobItem : INotifyPropertyChanged
{
    private readonly Action<QueueJobItem>? _onSpeedChanged;
    private bool _unlimitedSpeed = true;
    private string _maxMBpsText = "";
    private bool _suppressSpeed;
    private double _percent;
    private ProgressStats _stats = ProgressStats.Idle;
    private bool _canMoveUp;
    private bool _canMoveDown;

    public QueueJobItem(Job job, Action<QueueJobItem>? onSpeedChanged = null)
    {
        Job = job;
        _onSpeedChanged = onSpeedChanged;
        SyncSpeedFromJob();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Job Job { get; }

    public string Title => string.IsNullOrWhiteSpace(Job.Name) ? Job.Id[..8] : Job.Name;

    public string Route => $"{Job.SourcePath}  →  {Job.DestinationPath}";

    private bool _writingRundown;

    public string StatusLabel =>
        _writingRundown ? CopyPipeline.RundownLabel : JobDue.StatusLabel(Job, DateTimeOffset.Now);

    public TransferRundown Rundown => TransferRundown.From(Job);

    public bool HasRundown => Rundown.IsVisible;

    public string RundownLine => Rundown.OneLine;

    public string SettingsSummary
    {
        get
        {
            var o = Job.Options;
            var speed = o.MaxMegabytesPerSecond is > 0
                ? $"{o.MaxMegabytesPerSecond.Value.ToString("0.###", CultureInfo.InvariantCulture)} MB/s"
                : "Unlimited speed";
            var hours = o.HoursEnabled
                ? $"Window {o.HoursStart:HH:mm}–{o.HoursEnd:HH:mm}"
                : "Window off";
            var verify = o.Verify == VerifyLevel.Thorough ? "Thorough verify" : "Quick verify";
            var dry = o.DryRun ? "Dry Run" : o.PackAsZip ? "Small Files" : "Copy";
            var expand = o.IgnoreFreeSpaceCheck ? "Ignore Storage Limit" : "Storage limit on";
            var start = Job.ScheduledStart is { } s
                ? $"Start after {s.LocalDateTime:ddd d MMM HH:mm}"
                : "Start when previous job finishes";
            var flags = new List<string>();
            if (!o.CopyTimestamps)
            {
                flags.Add("timestamps off");
            }

            if (o.CopySecurity)
            {
                flags.Add("ACL");
            }

            if (o.CopyOwner)
            {
                flags.Add("owner");
            }

            if (o.UnbufferedIo)
            {
                flags.Add("force unbuffered");
            }

            if (o.PurgeExtraDestFiles)
            {
                flags.Add("purge extra dest");
            }

            if (!o.IncludeSourceFolderName)
            {
                flags.Add("contents only");
            }

            var robo = flags.Count == 0 ? "RoboFlags defaults" : "RoboFlags: " + string.Join(", ", flags);
            return $"{speed}. {hours}. {verify}. {dry}. {expand}. {robo}. {start}.";
        }
    }

    public IReadOnlyList<string> OptionBadges => JobOptionBadges.For(Job);

    public bool HasOptionBadges => OptionBadges.Count > 0;

    public double Percent
    {
        get => _percent;
        set => SetField(ref _percent, value);
    }

    public ProgressStats Stats
    {
        get => _stats;
        set => SetField(ref _stats, value);
    }

    public bool CanMoveUp
    {
        get => _canMoveUp;
        set => SetField(ref _canMoveUp, value);
    }

    public bool CanMoveDown
    {
        get => _canMoveDown;
        set => SetField(ref _canMoveDown, value);
    }

    public bool IsActive =>
        _writingRundown
        || Job.Status is JobStatus.Preparing or JobStatus.Enumerating or JobStatus.Copying or JobStatus.Verifying
            or JobStatus.Paused or JobStatus.PausedOutsideHours;

    public bool CanPause =>
        !_writingRundown
        && Job.Status is JobStatus.Enumerating or JobStatus.Copying or JobStatus.Verifying;

    public bool CanResume =>
        Job.Status == JobStatus.Pending
        || Job.OnHold
        || Job.Status is JobStatus.Paused or JobStatus.PausedOutsideHours
            or JobStatus.Cancelled or JobStatus.Incomplete or JobStatus.Failed;

    public string ResumeLabel => Job.Status == JobStatus.Pending ? "Start" : "Resume";

    public bool CanStop => IsActive;

    public bool CanRemove => Job.Status is JobStatus.Pending or JobStatus.Completed
        or JobStatus.Cancelled or JobStatus.Incomplete or JobStatus.Failed;

    public bool CanToggleHold => Job.Status == JobStatus.Pending;

    public string HoldLabel => Job.OnHold ? "Unhold" : "Hold";

    public bool CanEditSpeed =>
        Job.Status is JobStatus.Pending or JobStatus.Preparing or JobStatus.Enumerating or JobStatus.Copying
            or JobStatus.Verifying or JobStatus.Paused or JobStatus.PausedOutsideHours;

    public bool UnlimitedSpeed
    {
        get => _unlimitedSpeed;
        set
        {
            if (!SetField(ref _unlimitedSpeed, value))
            {
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEditLimitedSpeed)));
            if (!_suppressSpeed)
            {
                _onSpeedChanged?.Invoke(this);
            }
        }
    }

    public string MaxMBpsText
    {
        get => _maxMBpsText;
        set
        {
            if (!SetField(ref _maxMBpsText, value))
            {
                return;
            }

            if (!_suppressSpeed)
            {
                _onSpeedChanged?.Invoke(this);
            }
        }
    }

    public bool CanEditLimitedSpeed => CanEditSpeed && !UnlimitedSpeed;

    public void SyncSpeedFromJob()
    {
        _suppressSpeed = true;
        UnlimitedSpeed = Job.Options.MaxMegabytesPerSecond is null or <= 0;
        MaxMBpsText = Job.Options.MaxMegabytesPerSecond is > 0
            ? Job.Options.MaxMegabytesPerSecond.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : "";
        _suppressSpeed = false;
    }

    public void ApplyProgress(JobProgress progress)
    {
        _writingRundown = progress.IsRundownStage;
        Percent = progress.Percent;
        Stats = ProgressStats.From(progress);
        RaiseComputed();
    }

    public void RaiseComputed()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SettingsSummary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OptionBadges)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasOptionBadges)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPause)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanResume)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResumeLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStop)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRemove)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanToggleHold)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HoldLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEditSpeed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEditLimitedSpeed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Route)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rundown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RundownLine)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRundown)));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
