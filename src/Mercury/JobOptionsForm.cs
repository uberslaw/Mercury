using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Mercury;

/// <summary>
/// Same fields as Transfer → Job options, bound to a queue draft or a specific queued job.
/// </summary>
public sealed class JobOptionsForm : INotifyPropertyChanged
{
    private bool _loading;
    private bool _unlimitedSpeed = true;
    private string _maxMBpsText = "";
    private bool _hoursEnabled;
    private string _hoursStart = "08:00";
    private string _hoursEnd = "18:00";
    private string _retryCount = "3";
    private string _retryWait = "5";
    private int _overwriteIndex;
    private int _verifyIndex;
    private bool _dryRun;
    private bool _packAsZip;
    private bool _skipCompressedWhenPacking = true;
    private bool _ignoreFreeSpaceCheck;
    private bool _roboFlagsExpanded;
    private bool _copyTimestamps = true;
    private bool _copyAttributes = true;
    private bool _copySecurity;
    private bool _copyOwner;
    private bool _copyDirectoryTimestamps = true;
    private bool _copyEmptyDirectories = true;
    private bool _includeSourceFolderName = true;
    private bool _unbufferedIo;
    private bool _copySymbolicLinksAsLinks;
    private bool _fatTimestampTolerance;
    private bool _excludeHiddenSystem;
    private bool _purgeExtraDestFiles;
    private bool _scheduleEnabled;
    private DateTime? _scheduledDate = DateTime.Today;
    private string _scheduledTime = "09:00";
    private bool _showCatcherTemplate;
    private bool _showIncludeFolder = true;
    private bool _showSpeedInMegabits;
    private bool _canEditCatcher = true;
    private bool _showAddToQueue;
    private string _windowTitle = "Job options";
    private string _hint = "";
    private string _speedToolTip = "Saved on this job.";
    private string _previewSource = "";
    private string _previewDest = "";
    private CatcherEnvelope? _selectedCatcherTemplate;
    private IEnumerable<CatcherEnvelope> _catcherTemplates = [];

    public JobOptionsForm()
    {
        ToggleRoboFlagsCommand = new RelayCommand(() => RoboFlagsExpanded = !RoboFlagsExpanded);
        ApplyCommand = new RelayCommand(() => Applied?.Invoke());
        CloseCommand = new RelayCommand(() =>
        {
            Applied?.Invoke();
            CloseRequested?.Invoke();
        });
        AddToQueueCommand = new RelayCommand(() => AddToQueueRequested?.Invoke(), () => CanAddToQueue);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Applied;
    public event Action? CloseRequested;
    public event Action? AddToQueueRequested;
    public event Action? SpeedChanged;

    public Func<string, bool>? ConfirmPurge { get; set; }
    public Func<bool>? CanAdd { get; set; }

    public ICommand ToggleRoboFlagsCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand AddToQueueCommand { get; }

    public string WindowTitle { get => _windowTitle; set => SetField(ref _windowTitle, value); }
    public string Hint { get => _hint; set => SetField(ref _hint, value); }
    public string SpeedToolTip { get => _speedToolTip; set => SetField(ref _speedToolTip, value); }
    public bool ShowCatcherTemplate
    {
        get => _showCatcherTemplate;
        set
        {
            if (SetField(ref _showCatcherTemplate, value))
            {
                RaiseLanding();
            }
        }
    }
    public bool ShowIncludeFolder { get => _showIncludeFolder; set => SetField(ref _showIncludeFolder, value); }

    public string SpeedUnitLabel => BandwidthUnit.Label(ShowSpeedInMegabits);

    public bool ShowSpeedInMegabits
    {
        get => _showSpeedInMegabits;
        set
        {
            if (_showSpeedInMegabits == value)
            {
                return;
            }

            var from = _showSpeedInMegabits;
            _showSpeedInMegabits = value;
            var converted = BandwidthUnit.ConvertDisplayText(_maxMBpsText, from, value);
            var loading = _loading;
            _loading = true;
            _maxMBpsText = converted;
            _loading = loading;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSpeedInMegabits)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeedUnitLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxMBpsText)));
        }
    }

    public bool CanEditCatcher { get => _canEditCatcher; set => SetField(ref _canEditCatcher, value); }
    public bool ShowAddToQueue
    {
        get => _showAddToQueue;
        set
        {
            if (SetField(ref _showAddToQueue, value))
            {
                (AddToQueueCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanAddToQueue => ShowAddToQueue && (CanAdd?.Invoke() ?? false);

    public IEnumerable<CatcherEnvelope> CatcherTemplates
    {
        get => _catcherTemplates;
        set => SetField(ref _catcherTemplates, value);
    }

    public CatcherEnvelope? SelectedCatcherTemplate
    {
        get => _selectedCatcherTemplate;
        set => SetField(ref _selectedCatcherTemplate, value);
    }

    public bool UnlimitedSpeed
    {
        get => _unlimitedSpeed;
        set
        {
            if (SetField(ref _unlimitedSpeed, value))
            {
                NotifySpeed();
            }
        }
    }

    public string MaxMBpsText
    {
        get => _maxMBpsText;
        set
        {
            if (SetField(ref _maxMBpsText, value))
            {
                NotifySpeed();
            }
        }
    }

    public bool HoursEnabled { get => _hoursEnabled; set => SetField(ref _hoursEnabled, value); }
    public string HoursStart { get => _hoursStart; set => SetField(ref _hoursStart, value); }
    public string HoursEnd { get => _hoursEnd; set => SetField(ref _hoursEnd, value); }
    public string RetryCount { get => _retryCount; set => SetField(ref _retryCount, value); }
    public string RetryWait { get => _retryWait; set => SetField(ref _retryWait, value); }
    public int OverwriteIndex { get => _overwriteIndex; set => SetField(ref _overwriteIndex, value); }
    public int VerifyIndex { get => _verifyIndex; set => SetField(ref _verifyIndex, value); }
    public bool DryRun { get => _dryRun; set => SetField(ref _dryRun, value); }
    public bool PackAsZip { get => _packAsZip; set => SetField(ref _packAsZip, value); }
    public bool SkipCompressedWhenPacking { get => _skipCompressedWhenPacking; set => SetField(ref _skipCompressedWhenPacking, value); }
    public bool IgnoreFreeSpaceCheck { get => _ignoreFreeSpaceCheck; set => SetField(ref _ignoreFreeSpaceCheck, value); }
    public bool RoboFlagsExpanded { get => _roboFlagsExpanded; set => SetField(ref _roboFlagsExpanded, value); }
    public bool CopyTimestamps { get => _copyTimestamps; set => SetField(ref _copyTimestamps, value); }
    public bool CopyAttributes { get => _copyAttributes; set => SetField(ref _copyAttributes, value); }
    public bool CopySecurity { get => _copySecurity; set => SetField(ref _copySecurity, value); }
    public bool CopyOwner { get => _copyOwner; set => SetField(ref _copyOwner, value); }
    public bool CopyDirectoryTimestamps { get => _copyDirectoryTimestamps; set => SetField(ref _copyDirectoryTimestamps, value); }
    public bool CopyEmptyDirectories { get => _copyEmptyDirectories; set => SetField(ref _copyEmptyDirectories, value); }
    public bool IncludeSourceFolderName
    {
        get => _includeSourceFolderName;
        set
        {
            if (SetField(ref _includeSourceFolderName, value))
            {
                RaiseLanding();
            }
        }
    }
    public bool UnbufferedIo { get => _unbufferedIo; set => SetField(ref _unbufferedIo, value); }
    public bool CopySymbolicLinksAsLinks { get => _copySymbolicLinksAsLinks; set => SetField(ref _copySymbolicLinksAsLinks, value); }
    public bool FatTimestampTolerance { get => _fatTimestampTolerance; set => SetField(ref _fatTimestampTolerance, value); }
    public bool ExcludeHiddenSystem { get => _excludeHiddenSystem; set => SetField(ref _excludeHiddenSystem, value); }

    public bool PurgeExtraDestFiles
    {
        get => _purgeExtraDestFiles;
        set
        {
            if (value && !_purgeExtraDestFiles && !_loading && ConfirmPurge is not null
                && !ConfirmPurge("Turn on Purge extra dest files?"))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PurgeExtraDestFiles)));
                return;
            }

            SetField(ref _purgeExtraDestFiles, value);
        }
    }

    public bool ScheduleEnabled { get => _scheduleEnabled; set => SetField(ref _scheduleEnabled, value); }
    public DateTime? ScheduledDate { get => _scheduledDate; set => SetField(ref _scheduledDate, value); }
    public string ScheduledTime { get => _scheduledTime; set => SetField(ref _scheduledTime, value); }

    public string LandingPreview
    {
        get
        {
            if (ShowCatcherTemplate)
            {
                return string.IsNullOrWhiteSpace(_previewSource)
                    ? ""
                    : "Will land in: Catcher receive folder (named source folders keep their top folder).";
            }

            var path = CopyShape.PreviewLandingPath(_previewSource, _previewDest, IncludeSourceFolderName);
            return string.IsNullOrEmpty(path) ? "" : "Will land in: " + path;
        }
    }

    public bool ShowLandingPreview => !string.IsNullOrEmpty(LandingPreview);

    public void RaiseAddCanExecute() => (AddToQueueCommand as RelayCommand)?.RaiseCanExecuteChanged();

    public void LoadFrom(JobOptions options, DateTimeOffset? scheduledStart)
    {
        _loading = true;
        try
        {
            UnlimitedSpeed = options.MaxMegabytesPerSecond is null or <= 0;
            MaxMBpsText = options.MaxMegabytesPerSecond is > 0
                ? BandwidthUnit.FormatMegabytes(options.MaxMegabytesPerSecond.Value, ShowSpeedInMegabits)
                : "";
            HoursEnabled = options.HoursEnabled;
            HoursStart = options.HoursStart.ToString("HH:mm");
            HoursEnd = options.HoursEnd.ToString("HH:mm");
            RetryCount = options.RetryCount.ToString(CultureInfo.InvariantCulture);
            RetryWait = options.RetryWaitSeconds.ToString(CultureInfo.InvariantCulture);
            OverwriteIndex = options.Overwrite switch
            {
                OverwritePolicy.Always => 1,
                OverwritePolicy.NeverIfExists => 2,
                _ => 0
            };
            VerifyIndex = options.Verify == VerifyLevel.Thorough ? 1 : 0;
            DryRun = options.DryRun;
            PackAsZip = options.PackAsZip;
            SkipCompressedWhenPacking = options.SkipCompressedWhenPacking;
            IgnoreFreeSpaceCheck = options.IgnoreFreeSpaceCheck;
            CopyTimestamps = options.CopyTimestamps;
            CopyAttributes = options.CopyAttributes;
            CopySecurity = options.CopySecurity;
            CopyOwner = options.CopyOwner;
            CopyDirectoryTimestamps = options.CopyDirectoryTimestamps;
            CopyEmptyDirectories = options.CopyEmptyDirectories;
            IncludeSourceFolderName = options.IncludeSourceFolderName;
            UnbufferedIo = options.UnbufferedIo;
            CopySymbolicLinksAsLinks = options.CopySymbolicLinksAsLinks;
            FatTimestampTolerance = options.FatTimestampTolerance;
            ExcludeHiddenSystem = options.ExcludeHiddenSystem;
            PurgeExtraDestFiles = options.PurgeExtraDestFiles;
            ScheduleEnabled = scheduledStart is not null;
            if (scheduledStart is { } start)
            {
                ScheduledDate = start.LocalDateTime.Date;
                ScheduledTime = TimeOnly.FromDateTime(start.LocalDateTime).ToString("HH:mm");
            }
        }
        finally
        {
            _loading = false;
        }
    }

    public void LoadFrom(Job job, IEnumerable<CatcherEnvelope> templates)
    {
        CatcherTemplates = templates;
        LoadFrom(job.Options, job.ScheduledStart);
        ShowCatcherTemplate = job.Catcher is not null;
        CanEditCatcher = job.Status is JobStatus.Pending or JobStatus.Cancelled or JobStatus.Incomplete
            or JobStatus.Failed or JobStatus.Completed;
        SelectedCatcherTemplate = job.Catcher is { } catcher
            ? templates.FirstOrDefault(t => t.Id == catcher.TemplateId)
            : null;
        WindowTitle = "Job options — " + (string.IsNullOrWhiteSpace(job.Name) ? job.Id[..8] : job.Name);
        Hint = job.SourcePath + "  →  " + job.DestinationPath;
        SetLandingPaths(job.SourcePath, job.DestinationPath);
        SpeedToolTip = job.Status is JobStatus.Preparing or JobStatus.Enumerating or JobStatus.Copying
            or JobStatus.Verifying or JobStatus.Paused or JobStatus.PausedOutsideHours
            ? "Applies immediately to this running job."
            : "Saved on this queued job.";
        ShowAddToQueue = false;
    }

    public void PrepareDraft(
        IEnumerable<CatcherEnvelope> templates,
        bool destIsCatcher,
        CatcherEnvelope? catcher,
        string sourcePath,
        string destPath)
    {
        CatcherTemplates = templates;
        ShowCatcherTemplate = destIsCatcher;
        CanEditCatcher = true;
        SelectedCatcherTemplate = catcher;
        WindowTitle = "Job options — next queued job";
        Hint = "These options are stored on the job you Add. They stay independent of the Transfer tab, so you can change them while a copy is running.";
        SpeedToolTip = "Stored on the next job you Add to queue.";
        ShowAddToQueue = true;
        SetLandingPaths(sourcePath, destPath);
        RaiseAddCanExecute();
    }

    public void SetLandingPaths(string sourcePath, string destPath)
    {
        _previewSource = sourcePath ?? "";
        _previewDest = destPath ?? "";
        RaiseLanding();
    }

    public JobOptions ToOptions()
    {
        double? max = null;
        if (!UnlimitedSpeed && BandwidthUnit.TryParseMegabytes(MaxMBpsText, ShowSpeedInMegabits, out var mb))
        {
            max = mb;
        }

        TimeOnly.TryParse(HoursStart, out var start);
        TimeOnly.TryParse(HoursEnd, out var end);
        int.TryParse(RetryCount, out var retries);
        int.TryParse(RetryWait, out var wait);

        return new JobOptions
        {
            MaxMegabytesPerSecond = max,
            HoursEnabled = HoursEnabled,
            HoursStart = start == default ? new TimeOnly(8, 0) : start,
            HoursEnd = end == default ? new TimeOnly(18, 0) : end,
            RetryCount = Math.Max(0, retries),
            RetryWaitSeconds = Math.Max(1, wait == 0 ? 5 : wait),
            Overwrite = OverwriteIndex switch
            {
                1 => OverwritePolicy.Always,
                2 => OverwritePolicy.NeverIfExists,
                _ => OverwritePolicy.SkipIfNewerOrEqual
            },
            Verify = VerifyIndex == 1 ? VerifyLevel.Thorough : VerifyLevel.Quick,
            DryRun = DryRun,
            PackAsZip = PackAsZip,
            SkipCompressedWhenPacking = SkipCompressedWhenPacking,
            IgnoreFreeSpaceCheck = IgnoreFreeSpaceCheck,
            CopyTimestamps = CopyTimestamps,
            CopyAttributes = CopyAttributes,
            CopySecurity = CopySecurity,
            CopyOwner = CopyOwner,
            CopyDirectoryTimestamps = CopyDirectoryTimestamps,
            CopyEmptyDirectories = CopyEmptyDirectories,
            IncludeSourceFolderName = IncludeSourceFolderName,
            UnbufferedIo = UnbufferedIo,
            CopySymbolicLinksAsLinks = CopySymbolicLinksAsLinks,
            FatTimestampTolerance = FatTimestampTolerance,
            ExcludeHiddenSystem = ExcludeHiddenSystem,
            PurgeExtraDestFiles = PurgeExtraDestFiles
        };
    }

    public DateTimeOffset? ToScheduledStart()
    {
        if (!ScheduleEnabled || ScheduledDate is not { } date || !TimeOnly.TryParse(ScheduledTime, out var time))
        {
            return null;
        }

        var local = DateTime.SpecifyKind(date.Date.Add(time.ToTimeSpan()), DateTimeKind.Local);
        return new DateTimeOffset(local);
    }

    public void WriteTo(Job job)
    {
        job.Options.CopyFrom(ToOptions());
        job.ScheduledStart = ToScheduledStart();
        if (ShowCatcherTemplate && CanEditCatcher && SelectedCatcherTemplate is { } template)
        {
            job.Catcher = template.ToTarget();
            job.DestinationPath = CatcherCrypto.FormatDestination(template.PublicHost, template.PublicPort, template.Name);
        }
    }

    public IReadOnlyList<string> PreviewBadges(bool destIsCatcher, CatcherEnvelope? catcher, bool onHold = false)
    {
        var job = new Job
        {
            Options = ToOptions(),
            ScheduledStart = ToScheduledStart(),
            OnHold = onHold,
            Catcher = destIsCatcher ? catcher?.ToTarget() : null
        };
        return JobOptionBadges.For(job);
    }

    private void RaiseLanding()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LandingPreview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowLandingPreview)));
    }

    private void NotifySpeed()
    {
        if (!_loading)
        {
            SpeedChanged?.Invoke();
        }
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
