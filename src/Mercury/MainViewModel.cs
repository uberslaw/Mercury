using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Mercury;

public sealed class ConsoleLine
{
    public string Text { get; init; } = "";
    public bool IsError { get; init; }
}

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppPaths _paths;
    private readonly JobScheduler _scheduler;
    private string? _runningJobId;
    private Job? _lastJob;
    private JobProgress? _lastProgress;
    private string? _progressJobId;
    private DispatcherTimer? _elapsedTimer;

    private string _sourcePath = "";
    private string _destPath = "";
    private string _maxMBpsText = "";
    private bool _unlimitedSpeed = true;
    private bool _hoursEnabled;
    private string _hoursStart = "08:00";
    private string _hoursEnd = "18:00";
    private string _retryCount = "3";
    private string _retryWait = "5";
    private int _overwriteIndex;
    private int _verifyIndex;
    private bool _dryRun;
    private bool _packAsZip;
    private bool _ignoreFreeSpaceCheck;
    private bool _roboFlagsExpanded;
    private bool _applyingJobOptions;
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
    private string _globalMinText = "0";
    private string _globalMaxText = "";
    private bool _globalMaxUnlimited = true;
    private bool _idleThrottleEnabled;
    private string _idleThrottleText = "";
    private bool _showSpeedInMegabits;
    private bool _errorsOnly;
    private string _consoleSearch = "";
    private bool _followConsole = true;
    private string _statusText = "Pick a source and destination, then Start.";
    private string _resultBanner = "";
    private Brush _resultBrush = Brushes.Transparent;
    private string _currentFile = "";
    private ProgressStats _jobStats = ProgressStats.Idle;
    private ProgressStats _overallStats = ProgressStats.Idle;
    private TransferRundown _rundown = TransferRundown.Empty;
    private double _jobPercent;
    private double _overallPercent;
    private bool _isRunning;
    private bool _hasBackgroundRundown;
    private bool _isPaused;
    private bool _portableData;
    private string _helpSearch = "";
    private string _catcherOnlineStatus = "";
    private DispatcherTimer? _catcherProbeTimer;
    private bool _cloudWarning;
    private string _dataPathLabel = "";
    private bool _canResumeLast;
    private bool _scheduleEnabled;
    private DateTime? _scheduledDate = DateTime.Today;
    private string _scheduledTime = "09:00";
    private QueueJobItem? _selectedQueueJob;
    private HistoryItem? _selectedHistoryItem;
    private int _destKindIndex;
    private string _queueSourcePath = "";
    private string _queueDestPath = "";
    private int _queueDestKindIndex;
    private CatcherEnvelope? _queueSelectedCatcherTemplate;
    private bool _queueDraftSeeded;
    private bool _suppressQueueDraftSync;
    private JobOptionsForm? _jobForm;
    private QueueJobItem? _jobFormItem;
    private bool _skipCompressedWhenPacking = true;
    private string _catcherPassphrase = "";
    private CatcherEnvelope? _selectedCatcherTemplate;
    private string _networkName = "";
    private string _networkPublicHost = "";
    private string _networkPublicPort = "443";
    private string _networkInternalPort = "8443";
    private string _networkBindAddress = "";
    private string _networkFingerprint = "";
    private string _networkHint = "Router maps publicIP:publicPort → Catcher LAN:internalPort. Catcher must be listening. Blank bind IP = all interfaces (needed for NAT).";
    private string _networkStatus = "";
    private bool _closeAfterPause;
    private bool _keepHeartbeatOnClose;
    private bool _folderTreeEmpty = true;
    private DateTime _folderTreeRefreshUtc;
    private string? _folderTreeJobId;
    private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);

    public MainViewModel() : this(new AppPaths())
    {
    }

    public MainViewModel(AppPaths paths)
    {
        _paths = paths;
        _scheduler = new JobScheduler(paths);
        _scheduler.ProgressChanged += OnProgress;
        _scheduler.QueueChanged += OnQueueChanged;
        _scheduler.HistoryChanged += OnHistoryChanged;
        _scheduler.Log.LineWritten += OnLog;

        var settings = AppSettingsStore.Load(paths);
        _showSpeedInMegabits = settings.ShowSpeedInMegabits;
        if (settings.GlobalMinMegabytesPerSecond is > 0)
        {
            GlobalMinText = BandwidthUnit.FormatMegabytes(settings.GlobalMinMegabytesPerSecond.Value, _showSpeedInMegabits);
        }

        if (settings.GlobalMaxMegabytesPerSecond is > 0)
        {
            GlobalMaxUnlimited = false;
            GlobalMaxText = BandwidthUnit.FormatMegabytes(settings.GlobalMaxMegabytesPerSecond.Value, _showSpeedInMegabits);
        }

        if (settings.IdleThrottleMegabytesPerSecond is > 0)
        {
            IdleThrottleEnabled = true;
            IdleThrottleText = BandwidthUnit.FormatMegabytes(settings.IdleThrottleMegabytesPerSecond.Value, _showSpeedInMegabits);
        }

        Replace(NeverPackExtensions, settings.NeverPackExtensions ?? PackPolicy.DefaultNeverPackExtensions());
        Replace(PackExtensions, settings.PackExtensions);

        DataPathLabel = paths.IsPortable
            ? (paths.UsedLegacyPortable
                ? $"Data (beside exe, jobs found): {paths.DataRoot}"
                : $"Data (portable): {paths.DataRoot}")
            : $"Data: {paths.DataRoot}";
        _portableData = paths.IsPortable && !paths.UsedLegacyPortable;
        foreach (var row in paths.ListWritePaths())
        {
            DataPaths.Add(new SettingsPathItem(row));
        }

        foreach (var section in HelpDocument.Sections)
        {
            HelpSections.Add(section);
        }

        StartCommand = new RelayCommand(StartNow, CanStartOrResume);
        AddToQueueCommand = new RelayCommand(AddToQueue, () => HasPaths);
        QueueAddToQueueCommand = new RelayCommand(AddToQueueFromQueue, () => HasQueuePaths);
        PauseCommand = new RelayCommand(Pause, () => IsRunning && !IsPaused);
        PauseAfterThisFileCommand = new RelayCommand(PauseAfterThisFile, CanPauseAfterThisFile);
        ResumePausedCommand = new RelayCommand(ResumePaused, () => IsRunning && IsPaused);
        StopCommand = new RelayCommand(Stop, () => IsRunning || HasBackgroundRundown);
        ResumeLastCommand = new RelayCommand(ResumeLast, () => !IsRunning && CanResumeLast);
        BrowseSourceCommand = new RelayCommand(BrowseSource, () => PathsEditable);
        BrowseDestCommand = new RelayCommand(BrowseDest, () => PathsEditable);
        OpenPackExtensionsCommand = new RelayCommand(OpenPackExtensions);
        AddSourceCommand = new RelayCommand(AddSourceFromDraft, () => PathsEditable);
        RemoveSourceCommand = new RelayCommand(p => RemoveSource(p as SourceFolderItem), () => PathsEditable);
        ClearSourcesCommand = new RelayCommand(ClearSources, () => PathsEditable && SourceFolders.Count > 0);
        AddQueueSourceCommand = new RelayCommand(AddQueueSourceFromDraft);
        RemoveQueueSourceCommand = new RelayCommand(p => RemoveQueueSource(p as SourceFolderItem));
        ClearQueueSourcesCommand = new RelayCommand(ClearQueueSources, () => QueueSourceFolders.Count > 0);
        AddNeverPackCommand = new RelayCommand(AddNeverPack);
        RemoveNeverPackCommand = new RelayCommand(RemoveNeverPack);
        ResetNeverPackCommand = new RelayCommand(ResetNeverPack);
        AddPackExtensionCommand = new RelayCommand(AddPackExtension);
        RemovePackExtensionCommand = new RelayCommand(RemovePackExtension);
        PauseAllCommand = new RelayCommand(PauseAll, () => IsRunning && !IsGlobalPaused);
        ResumeAllCommand = new RelayCommand(ResumeAll, () => IsRunning && IsGlobalPaused);
        SaveJobCommand = new RelayCommand(SaveCurrentJob, () => !string.IsNullOrWhiteSpace(SourcePath) && (DestIsCatcher ? SelectedCatcherTemplate is not null : !string.IsNullOrWhiteSpace(DestPath)));
        ToggleRoboFlagsCommand = new RelayCommand(() => RoboFlagsExpanded = !RoboFlagsExpanded);
        LoadSavedJobCommand = new RelayCommand(p => LoadSavedJob(p as SavedJob));
        DeleteSavedJobCommand = new RelayCommand(p => DeleteSavedJob(p as SavedJob));
        OpenLogsCommand = new RelayCommand(OpenLogs);
        CopyConsoleCommand = new RelayCommand(CopyConsole);
        FollowLatestCommand = new RelayCommand(FollowLatest);
        PauseQueueJobCommand = new RelayCommand(p => PauseQueueJob(p as QueueJobItem));
        ResumeQueueJobCommand = new RelayCommand(p => ResumeQueueJob(p as QueueJobItem));
        StopQueueJobCommand = new RelayCommand(p => StopQueueJob(p as QueueJobItem));
        RemoveQueueJobCommand = new RelayCommand(p => RemoveQueueJob(p as QueueJobItem));
        MoveQueueJobUpCommand = new RelayCommand(p => MoveQueueJob(p as QueueJobItem, -1));
        MoveQueueJobDownCommand = new RelayCommand(p => MoveQueueJob(p as QueueJobItem, 1));
        ToggleHoldQueueJobCommand = new RelayCommand(p => ToggleHoldQueueJob(p as QueueJobItem));
        OpenQueueDraftOptionsCommand = new RelayCommand(OpenQueueDraftOptions);
        OpenQueueJobOptionsCommand = new RelayCommand(p => OpenQueueJobOptions(p as QueueJobItem));
        OpenSelectedQueueJobOptionsCommand = new RelayCommand(
            () => OpenQueueJobOptions(SelectedQueueJob),
            () => HasSelectedQueueJob);
        CreateCatcherCommand = new RelayCommand(CreateCatcherTemplate);
        ExportCatcherCommand = new RelayCommand(ExportCatcherTemplate, () => SelectedCatcherTemplate is not null);
        ImportCatcherCommand = new RelayCommand(ImportCatcherTemplate);
        DeleteCatcherCommand = new RelayCommand(DeleteCatcherTemplate, () => SelectedCatcherTemplate is not null);

        QueueDraft = new JobOptionsForm
        {
            ConfirmPurge = ConfirmPurge,
            CanAdd = () => HasQueuePaths,
            ShowSpeedInMegabits = _showSpeedInMegabits
        };
        QueueDraft.AddToQueueRequested += AddToQueueFromQueue;
        QueueDraft.Applied += RaiseQueueDraftBadges;
        QueueDraft.PropertyChanged += OnQueueDraftPropertyChanged;

        ReloadLibrary();
        ReloadCatcherTemplates();
        SyncQueue(rebuild: true);
        ReloadHistory();

        _lastJob = _scheduler.TryLoadLastJob();
        RefreshResume();
        if (_lastJob is { } last && IsResumable(last.Status))
        {
            SourcePath = last.SourcePath;
            DestPath = last.DestinationPath;
            ApplyJobOptions(last.Options);
            ApplyResumableSnapshot(last);
        }

        RefreshFolderTree(force: true);

        _scheduler.Kick();
        Theme = new ThemeViewModel();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ConsoleLine> ConsoleLines { get; } = [];
    public ObservableCollection<string> RecentSources { get; } = [];
    public ObservableCollection<string> RecentDestinations { get; } = [];
    public ObservableCollection<SavedJob> SavedJobs { get; } = [];
    public ObservableCollection<QueueJobItem> QueueJobs { get; } = [];
    public ObservableCollection<HistoryItem> HistoryItems { get; } = [];
    public ObservableCollection<CatcherEnvelope> CatcherTemplates { get; } = [];
    public ObservableCollection<SettingsPathItem> DataPaths { get; } = [];
    public ObservableCollection<HelpSection> HelpSections { get; } = [];
    public ObservableCollection<FolderTreeItem> FolderTree { get; } = [];
    public ICollectionView ConsoleView { get; private set; } = CollectionViewSource.GetDefaultView(Array.Empty<ConsoleLine>());

    public ICommand StartCommand { get; }
    public ICommand AddToQueueCommand { get; }
    public ICommand QueueAddToQueueCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand PauseAfterThisFileCommand { get; }
    public ICommand ResumePausedCommand { get; }
    public ICommand PauseAllCommand { get; }
    public ICommand ResumeAllCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ResumeLastCommand { get; }
    public ICommand BrowseSourceCommand { get; }
    public ICommand BrowseDestCommand { get; }
    public ICommand BrowseQueueSourceCommand { get; }
    public ICommand BrowseQueueDestCommand { get; }
    public ICommand SaveJobCommand { get; }
    public ICommand ToggleRoboFlagsCommand { get; }
    public ICommand LoadSavedJobCommand { get; }
    public ICommand DeleteSavedJobCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand CopyConsoleCommand { get; }
    public ICommand FollowLatestCommand { get; }
    public ICommand PauseQueueJobCommand { get; }
    public ICommand ResumeQueueJobCommand { get; }
    public ICommand StopQueueJobCommand { get; }
    public ICommand RemoveQueueJobCommand { get; }
    public ICommand MoveQueueJobUpCommand { get; }
    public ICommand MoveQueueJobDownCommand { get; }
    public ICommand ToggleHoldQueueJobCommand { get; }
    public ICommand OpenQueueDraftOptionsCommand { get; }
    public ICommand OpenQueueJobOptionsCommand { get; }
    public ICommand OpenSelectedQueueJobOptionsCommand { get; }
    public ICommand CreateCatcherCommand { get; }
    public ICommand ExportCatcherCommand { get; }
    public ICommand ImportCatcherCommand { get; }
    public ICommand DeleteCatcherCommand { get; }

    public ThemeViewModel Theme { get; }
    public JobOptionsForm QueueDraft { get; }

    public Action? CloseWindowRequested { get; set; }
    public Action<JobOptionsForm>? OpenJobOptionsRequested { get; set; }

    public string SourcePath
    {
        get => _sourcePath;
        set
        {
            if (SetField(ref _sourcePath, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPaths)));
                RaiseLandingPreview();
                RaiseRunCommands();
            }
        }
    }

    public string DestPath
    {
        get => _destPath;
        set
        {
            if (SetField(ref _destPath, value))
            {
                CloudWarning = DestIsFolder && CloudPath.LooksLikeCloudFolder(value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPaths)));
                RaiseLandingPreview();
                RaiseRunCommands();
            }
        }
    }

    public int DestKindIndex
    {
        get => _destKindIndex;
        set
        {
            if (SetField(ref _destKindIndex, value))
            {
                CloudWarning = DestIsFolder && CloudPath.LooksLikeCloudFolder(DestPath);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DestIsCatcher)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DestIsFolder)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPaths)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CatcherTransferHint)));
                RaiseLandingPreview();
                RaiseRunCommands();
                EnsureCatcherProbe();
            }
        }
    }

    public string QueueSourcePath
    {
        get => _queueSourcePath;
        set
        {
            if (SetField(ref _queueSourcePath, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasQueuePaths)));
                QueueDraft.SetLandingPaths(QueueSourcePath, QueueDestPath);
                QueueDraft.RaiseAddCanExecute();
                RaiseQueueDraftBadges();
                RaiseRunCommands();
            }
        }
    }

    public string QueueDestPath
    {
        get => _queueDestPath;
        set
        {
            if (SetField(ref _queueDestPath, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasQueuePaths)));
                QueueDraft.SetLandingPaths(QueueSourcePath, QueueDestPath);
                QueueDraft.RaiseAddCanExecute();
                RaiseQueueDraftBadges();
                RaiseRunCommands();
            }
        }
    }

    public int QueueDestKindIndex
    {
        get => _queueDestKindIndex;
        set
        {
            if (SetField(ref _queueDestKindIndex, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueDestIsCatcher)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueDestIsFolder)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasQueuePaths)));
                QueueDraft.ShowCatcherTemplate = QueueDestIsCatcher;
                RaiseQueueDraftBadges();
                QueueDraft.RaiseAddCanExecute();
                RaiseRunCommands();
            }
        }
    }

    public CatcherEnvelope? QueueSelectedCatcherTemplate
    {
        get => _queueSelectedCatcherTemplate;
        set
        {
            if (SetField(ref _queueSelectedCatcherTemplate, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasQueuePaths)));
                if (!_suppressQueueDraftSync)
                {
                    QueueDraft.SelectedCatcherTemplate = value;
                }

                RaiseQueueDraftBadges();
                QueueDraft.RaiseAddCanExecute();
                RaiseRunCommands();
            }
        }
    }

    public CatcherEnvelope? SelectedCatcherTemplate
    {
        get => _selectedCatcherTemplate;
        set
        {
            if (SetField(ref _selectedCatcherTemplate, value))
            {
                if (value is not null)
                {
                    NetworkName = value.Name;
                    NetworkPublicHost = value.PublicHost;
                    NetworkPublicPort = value.PublicPort.ToString(CultureInfo.InvariantCulture);
                    NetworkInternalPort = value.InternalListenPort.ToString(CultureInfo.InvariantCulture);
                    NetworkBindAddress = value.BindAddress;
                    NetworkFingerprint = value.FingerprintSha256;
                    NetworkHint = value.PortForwardHint();
                }

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPaths)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CatcherTransferHint)));
                RaiseLandingPreview();
                RaiseRunCommands();
                (ExportCatcherCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (DeleteCatcherCommand as RelayCommand)?.RaiseCanExecuteChanged();
                EnsureCatcherProbe();
            }
        }
    }

    public string CatcherTransferHint
    {
        get
        {
            if (!DestIsCatcher)
            {
                return "";
            }

            var route = SelectedCatcherTemplate is { } t
                ? t.PortForwardHint()
                : "Create a template on the Network tab first.";
            return route + " v1 sends one zip pack (folders) or the source file as-is. Catcher unpacks the zip into a folder tree on that PC. Per-file HTTPS resume is not in this version — restart the send if it fails. TLS fingerprint pinning is required; TLS cannot be turned off.";
        }
    }

    public string NetworkName { get => _networkName; set => SetField(ref _networkName, value); }
    public string NetworkPublicHost { get => _networkPublicHost; set => SetField(ref _networkPublicHost, value); }
    public string NetworkPublicPort { get => _networkPublicPort; set => SetField(ref _networkPublicPort, value); }
    public string NetworkInternalPort { get => _networkInternalPort; set => SetField(ref _networkInternalPort, value); }
    public string NetworkBindAddress { get => _networkBindAddress; set => SetField(ref _networkBindAddress, value); }
    public string NetworkFingerprint { get => _networkFingerprint; set => SetField(ref _networkFingerprint, value); }
    public string NetworkHint { get => _networkHint; set => SetField(ref _networkHint, value); }
    public string NetworkStatus { get => _networkStatus; set => SetField(ref _networkStatus, value); }

    public void SetCatcherPassphrase(string value)
    {
        _catcherPassphrase = value ?? "";
        EnsureCatcherProbe();
    }

    public string MaxMBpsText
    {
        get => _maxMBpsText;
        set
        {
            if (SetField(ref _maxMBpsText, value))
            {
                ApplyLiveSpeed();
            }
        }
    }

    public bool UnlimitedSpeed
    {
        get => _unlimitedSpeed;
        set
        {
            if (SetField(ref _unlimitedSpeed, value))
            {
                ApplyLiveSpeed();
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
                RaiseLandingPreview();
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
            if (value && !_purgeExtraDestFiles && !_applyingJobOptions && !ConfirmPurge("Turn on Purge extra dest files?"))
            {
                return;
            }

            SetField(ref _purgeExtraDestFiles, value);
        }
    }
    public string GlobalMinText
    {
        get => _globalMinText;
        set
        {
            if (SetField(ref _globalMinText, value))
            {
                ApplyLiveSpeed();
            }
        }
    }

    public string GlobalMaxText
    {
        get => _globalMaxText;
        set
        {
            if (SetField(ref _globalMaxText, value))
            {
                ApplyLiveSpeed();
            }
        }
    }

    public bool GlobalMaxUnlimited
    {
        get => _globalMaxUnlimited;
        set
        {
            if (SetField(ref _globalMaxUnlimited, value))
            {
                ApplyLiveSpeed();
            }
        }
    }

    public bool IdleThrottleEnabled
    {
        get => _idleThrottleEnabled;
        set
        {
            if (SetField(ref _idleThrottleEnabled, value))
            {
                ApplyLiveSpeed();
            }
        }
    }

    public string IdleThrottleText
    {
        get => _idleThrottleText;
        set
        {
            if (SetField(ref _idleThrottleText, value))
            {
                ApplyLiveSpeed();
            }
        }
    }

    public bool ErrorsOnly
    {
        get => _errorsOnly;
        set
        {
            if (SetField(ref _errorsOnly, value))
            {
                ConsoleView.Refresh();
            }
        }
    }

    public string ConsoleSearch
    {
        get => _consoleSearch;
        set
        {
            var next = value ?? "";
            if (!SetField(ref _consoleSearch, next))
            {
                return;
            }

            if (!string.IsNullOrEmpty(next))
            {
                FollowConsole = false;
            }

            ConsoleView.Refresh();

            if (string.IsNullOrEmpty(next))
            {
                FollowConsole = true;
            }
        }
    }

    public bool FollowConsole
    {
        get => _followConsole;
        set => SetField(ref _followConsole, value);
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetField(ref _statusText, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowHeaderStatus)));
            }
        }
    }
    public string ResultBanner
    {
        get => _resultBanner;
        set
        {
            if (SetField(ref _resultBanner, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowProgressDetail)));
            }
        }
    }
    public Brush ResultBrush { get => _resultBrush; set => SetField(ref _resultBrush, value); }
    public string CurrentFile { get => _currentFile; set => SetField(ref _currentFile, value); }
    public ProgressStats JobStats
    {
        get => _jobStats;
        set
        {
            if (SetField(ref _jobStats, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderStats)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowProgressDetail)));
            }
        }
    }
    public ProgressStats OverallStats { get => _overallStats; set => SetField(ref _overallStats, value); }
    public TransferRundown Rundown
    {
        get => _rundown;
        set
        {
            if (SetField(ref _rundown, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderRundown)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderRundownLine)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowHeaderRundown)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowProgressDetail)));
            }
        }
    }
    public double JobPercent
    {
        get => _jobPercent;
        set
        {
            if (SetField(ref _jobPercent, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(JobPercentText)));
            }
        }
    }

    public double OverallPercent
    {
        get => _overallPercent;
        set
        {
            if (SetField(ref _overallPercent, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OverallPercentText)));
            }
        }
    }

    public string JobPercentText => ProgressHeader.PercentLabel(JobPercent, JobStats.Bytes.HasValue || JobStats.Files.HasValue);

    public string OverallPercentText => ProgressHeader.PercentLabel(OverallPercent, JobStats.Bytes.HasValue || JobStats.Files.HasValue);

    public bool ShowOverallProgress => ProgressHeader.ShowOverall(QueueJobs.Count);
    public ProgressStats HeaderStats => JobStats;
    public TransferRundown HeaderRundown => TransferRundown.Empty;
    public string HeaderRundownLine => "";
    public string HeaderElapsedText => JobStats.Elapsed.HasValue ? JobStats.Elapsed.Value : "";
    public bool ShowHeaderElapsed => ShowProgressDetail && !string.IsNullOrWhiteSpace(HeaderElapsedText);
    public string HeaderEtaText => JobStats.Eta.HasValue ? JobStats.Eta.Value : "";
    public bool ShowHeaderEta => ShowProgressDetail && JobStats.Eta.HasValue;
    public bool ShowHeaderStatus =>
        ShowProgressDetail
        && !string.IsNullOrWhiteSpace(StatusText)
        && !ProgressHeader.IsExceptionDump(StatusText);
    public bool ShowHeaderRundown => false;
    public bool ShowProgressDetail =>
        IsRunning
        || HasBackgroundRundown
        || !string.IsNullOrWhiteSpace(ResultBanner)
        || JobPercent > 0.05
        || JobStats.Files.HasValue
        || JobStats.Bytes.HasValue
        || JobStats.Stage.HasValue;

    public bool FolderTreeEmpty
    {
        get => _folderTreeEmpty;
        internal set => SetField(ref _folderTreeEmpty, value);
    }

    public string StartButtonLabel => ShouldLabelResume ? "Resume" : "Start";

    public string PauseAfterThisFileLabel =>
        PauseAfterFileArmed ? "Remove Pause after" : "Pause after this file";

    public string PauseAfterThisFileToolTip =>
        PauseAfterFileArmed
            ? "Cancel the pending pause. The current file will keep transferring."
            : "Pauses after the file currently in flight completes. Does not cut the temp copy mid-stream.";

    public bool PauseAfterFileArmed =>
        _runningJobId is not null && _scheduler.IsPauseAfterFilePending(_runningJobId);

    public bool PortableData
    {
        get => _portableData;
        set
        {
            if (!SetField(ref _portableData, value))
            {
                return;
            }

            AppPaths.SetPortablePreferred(_paths.ExeDirectory, value);
        }
    }

    public string DataRootSummary =>
        _paths.UsedLegacyPortable
            ? "Using data beside the exe because it has job journals and AppData does not. Uncheck portable and restart Mercury to use %APPDATA%\\Mercury after this job is finished."
            : _paths.IsPortable
                ? "Portable mode: data is beside Mercury.exe. Default is %APPDATA%\\Mercury (takes effect on next launch if you uncheck portable)."
                : "Data is in %APPDATA%\\Mercury so logs and journals survive swapping the exe.";

    public string HelpSearch
    {
        get => _helpSearch;
        set
        {
            if (!SetField(ref _helpSearch, value))
            {
                return;
            }

            HelpSections.Clear();
            foreach (var section in HelpDocument.Search(value))
            {
                HelpSections.Add(section);
            }
        }
    }

    public string CatcherOnlineStatus
    {
        get => _catcherOnlineStatus;
        set => SetField(ref _catcherOnlineStatus, value);
    }
    public bool CloudWarning { get => _cloudWarning; set => SetField(ref _cloudWarning, value); }
    public string DataPathLabel { get => _dataPathLabel; set => SetField(ref _dataPathLabel, value); }
    public bool CanResumeLast { get => _canResumeLast; set => SetField(ref _canResumeLast, value); }
    public bool HasRecentSources => RecentSources.Count > 0;
    public bool HasRecentDestinations => RecentDestinations.Count > 0;
    public bool HasPaths =>
        !string.IsNullOrWhiteSpace(SourcePath) &&
        (DestIsCatcher ? SelectedCatcherTemplate is not null : !string.IsNullOrWhiteSpace(DestPath));
    public bool HasQueuePaths =>
        !string.IsNullOrWhiteSpace(QueueSourcePath) &&
        (QueueDestIsCatcher ? QueueSelectedCatcherTemplate is not null : !string.IsNullOrWhiteSpace(QueueDestPath));
    public bool DestIsCatcher => DestKindIndex == 1;
    public bool DestIsFolder => !DestIsCatcher;
    public bool QueueDestIsCatcher => QueueDestKindIndex == 1;
    public bool QueueDestIsFolder => !QueueDestIsCatcher;
    public bool ShowCatcherTemplate => false;
    public bool ShowIncludeFolder => false;
    public bool CanEditCatcher => true;
    public string SpeedToolTip => "Applies immediately to the running job.";
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
            _globalMinText = BandwidthUnit.ConvertDisplayText(_globalMinText, from, value);
            _globalMaxText = BandwidthUnit.ConvertDisplayText(_globalMaxText, from, value);
            _idleThrottleText = BandwidthUnit.ConvertDisplayText(_idleThrottleText, from, value);
            _maxMBpsText = BandwidthUnit.ConvertDisplayText(_maxMBpsText, from, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowSpeedInMegabits)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeedUnitLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalMinText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalMaxText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IdleThrottleText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxMBpsText)));
            QueueDraft.ShowSpeedInMegabits = value;
            if (_jobForm is not null)
            {
                _jobForm.ShowSpeedInMegabits = value;
            }

            SaveBandwidth();
            if (_lastProgress is not null)
            {
                JobStats = ComposeStats(_lastProgress);
            }

            NotifyHeader();
        }
    }
    public IReadOnlyList<string> QueueDraftBadges =>
        QueueDraft.PreviewBadges(QueueDestIsCatcher, QueueSelectedCatcherTemplate);
    public bool HasQueueDraftBadges => QueueDraftBadges.Count > 0;
    public string QueueLandingPreview
    {
        get
        {
            if (QueueDestIsCatcher)
            {
                return string.IsNullOrWhiteSpace(QueueSourcePath)
                    ? ""
                    : "Will land in: Catcher receive folder (named source folders keep their top folder).";
            }

            var path = CopyShape.PreviewLandingPath(
                QueueSourcePath,
                QueueDestPath,
                QueueDraft.IncludeSourceFolderName);
            return string.IsNullOrEmpty(path) ? "" : "Will land in: " + path;
        }
    }
    public bool ShowQueueLandingPreview => !string.IsNullOrEmpty(QueueLandingPreview);
    public bool IncludeSourceFolderNameApplies
    {
        get
        {
            if (!DestIsFolder || string.IsNullOrWhiteSpace(SourcePath))
            {
                return false;
            }

            try
            {
                return CopyShape.DetectKind(SourcePath) == SourceKind.Folder;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    public string LandingPreview
    {
        get
        {
            if (DestIsCatcher)
            {
                if (string.IsNullOrWhiteSpace(SourcePath) || SelectedCatcherTemplate is null)
                {
                    return "";
                }

                return "Will land in: Catcher receive folder (named source folders keep their top folder).";
            }

            if (string.IsNullOrWhiteSpace(SourcePath) || string.IsNullOrWhiteSpace(DestPath))
            {
                return "";
            }

            var path = CopyShape.PreviewLandingPath(SourcePath, DestPath, IncludeSourceFolderName);
            return string.IsNullOrEmpty(path) ? "" : "Will land in: " + path;
        }
    }

    public bool ShowLandingPreview => !string.IsNullOrEmpty(LandingPreview);
    public bool HasCatcherTemplates => CatcherTemplates.Count > 0;
    public bool HasQueueJobs => QueueJobs.Count > 0;
    public bool HasHistoryItems => HistoryItems.Count > 0;
    public bool HasSavedJobs => SavedJobs.Count > 0;

    public bool ScheduleEnabled
    {
        get => _scheduleEnabled;
        set => SetField(ref _scheduleEnabled, value);
    }

    public DateTime? ScheduledDate
    {
        get => _scheduledDate;
        set => SetField(ref _scheduledDate, value);
    }

    public string ScheduledTime
    {
        get => _scheduledTime;
        set => SetField(ref _scheduledTime, value);
    }

    public QueueJobItem? SelectedQueueJob
    {
        get => _selectedQueueJob;
        set
        {
            if (SetField(ref _selectedQueueJob, value))
            {
                _selectedQueueJob?.SyncSpeedFromJob();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedQueueJob)));
                (OpenSelectedQueueJobOptionsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedQueueJob => SelectedQueueJob is not null;

    public HistoryItem? SelectedHistoryItem
    {
        get => _selectedHistoryItem;
        set
        {
            if (SetField(ref _selectedHistoryItem, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedHistoryItem)));
                if (value is not null && !IsRunning)
                {
                    ApplyRundown(value.Job);
                    if (!string.IsNullOrWhiteSpace(value.Job.ResultMessage))
                    {
                        ResultBanner = value.Job.ResultMessage!;
                        ResultBrush = value.Job.Status switch
                        {
                            JobStatus.Completed => (Brush)Application.Current.FindResource("OkBrush"),
                            JobStatus.Incomplete or JobStatus.Failed => (Brush)Application.Current.FindResource("DangerBrush"),
                            JobStatus.Cancelled => (Brush)Application.Current.FindResource("WarnBrush"),
                            _ => (Brush)Application.Current.FindResource("MutedBrush")
                        };
                    }
                }
            }
        }
    }

    public bool HasSelectedHistoryItem => SelectedHistoryItem is not null;

    public bool PathsEditable => !IsRunning || IsPaused;

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetField(ref _isRunning, value))
            {
                RaiseRunCommands();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowProgressDetail)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowHeaderRundown)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PathsEditable)));
            }
        }
    }

    public bool HasBackgroundRundown
    {
        get => _hasBackgroundRundown;
        set
        {
            if (SetField(ref _hasBackgroundRundown, value))
            {
                RaiseRunCommands();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowProgressDetail)));
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (SetField(ref _isPaused, value))
            {
                RaiseRunCommands();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PathsEditable)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartButtonLabel)));
            }
        }
    }

    private bool _isGlobalPaused;

    public bool IsGlobalPaused
    {
        get => _isGlobalPaused;
        set
        {
            if (SetField(ref _isGlobalPaused, value))
            {
                RaiseRunCommands();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalPauseLabel)));
            }
        }
    }

    public string GlobalPauseLabel => IsGlobalPaused ? "Resume all" : "Pause all";

    public void AttachConsoleView(ICollectionView view)
    {
        ConsoleView = view;
        ConsoleView.Filter = MatchesConsole;
    }

    public void SetDroppedSource(string path)
    {
        if (!PathsEditable)
        {
            return;
        }

        SourcePath = path;
        RememberPath(isSource: true, path);
    }

    public void SetDroppedDest(string path)
    {
        if (!PathsEditable)
        {
            return;
        }

        DestPath = path;
        RememberPath(isSource: false, path);
    }

    public void Closing()
    {
        CatcherSession.Clear();
        _scheduler.StopAll(clearHeartbeat: !_keepHeartbeatOnClose);
    }

    public void OfferDirtyResume(Window owner)
    {
        var dirty = _scheduler.FindDirtyHeartbeat();
        if (dirty is null)
        {
            return;
        }

        var job = _scheduler.TryLoadJob(dirty.JobId) ?? _scheduler.TryLoadLastJob();
        if (job is null)
        {
            return;
        }

        SourcePath = job.SourcePath;
        if (job.Catcher is null)
        {
            DestPath = job.DestinationPath;
        }

        ApplyJobOptions(job.Options);
        ApplyRundown(job);
        var answer = MessageBox.Show(
            owner,
            JobHeartbeat.UnscheduledStopMessage(dirty),
            "Mercury",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            ResultBanner = "Unscheduled stop left a journal. Use Resume last when you want to continue.";
            ResultBrush = (Brush)Application.Current.FindResource("WarnBrush");
            RefreshResume();
            return;
        }

        ResumeStoredJob(job);
    }

    public bool ConfirmClose(Window owner)
    {
        if (!_scheduler.HasRunningJob)
        {
            return true;
        }

        var jobId = _runningJobId;
        if (!string.IsNullOrEmpty(jobId) && _scheduler.CanPauseAfterFile(jobId))
        {
            var eta = _scheduler.EstimateCurrentFileEta(jobId);
            var choice = ChoiceWindow.Show(
                owner,
                "Transfers in progress",
                JobHeartbeat.CloseWhileRunningMessage(eta),
                JobHeartbeat.WaitForThisFileLabel(eta),
                "Close now",
                "Cancel");
            if (choice == ChoiceResult.Cancel)
            {
                return false;
            }

            if (choice == ChoiceResult.Secondary)
            {
                _scheduler.Stop(jobId, clearHeartbeat: false);
                _keepHeartbeatOnClose = true;
                return true;
            }

            if (IsPaused)
            {
                _keepHeartbeatOnClose = true;
                return true;
            }

            _closeAfterPause = true;
            _scheduler.RequestPauseAfterFile(jobId);
            StatusText = "Waiting for the current file to finish, then Mercury will close.";
            NotifyPauseAfter();
            return false;
        }

        var prep = ChoiceWindow.Show(
            owner,
            "Transfers in progress",
            "You have a transfer running. Close now and resume later, or stay open?",
            "Close now",
            secondary: null,
            cancel: "Cancel");
        if (prep != ChoiceResult.Primary)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(jobId))
        {
            _scheduler.Stop(jobId, clearHeartbeat: false);
        }

        _keepHeartbeatOnClose = true;
        return true;
    }

    public void Dispose()
    {
        _scheduler.ProgressChanged -= OnProgress;
        _scheduler.QueueChanged -= OnQueueChanged;
        _scheduler.HistoryChanged -= OnHistoryChanged;
        _scheduler.Log.LineWritten -= OnLog;
        if (_elapsedTimer is not null)
        {
            _elapsedTimer.Tick -= OnElapsedTick;
            _elapsedTimer.Stop();
        }

        if (_catcherProbeTimer is not null)
        {
            _catcherProbeTimer.Tick -= OnCatcherProbeTick;
            _catcherProbeTimer.Stop();
        }
        _scheduler.Dispose();
        QueueDraft.AddToQueueRequested -= AddToQueueFromQueue;
        QueueDraft.Applied -= RaiseQueueDraftBadges;
        QueueDraft.PropertyChanged -= OnQueueDraftPropertyChanged;
        if (_jobForm is not null)
        {
            _jobForm.Applied -= OnJobFormApplied;
            _jobForm.SpeedChanged -= OnJobFormSpeed;
        }
    }

    private bool CanStartOrResume() =>
        HasPaths && (!IsRunning || IsPaused);

    private bool ShouldLabelResume =>
        IsPaused || (!IsRunning && HasPaths && FindMatchingResumableForUi() is not null);

    private void StartNow()
    {
        if (IsPaused)
        {
            ResumePaused();
            return;
        }

        if (DestIsCatcher)
        {
            try
            {
                CatcherCrypto.ValidatePassphrase(_catcherPassphrase);
            }
            catch (Exception ex)
            {
                StatusText = ex.Message;
                return;
            }
        }

        var job = BuildJob();
        if (!ConfirmPurgeIfJobNeedsIt(job))
        {
            return;
        }

        var existing = FindMatchingResumable(job);
        if (existing is not null)
        {
            if (_scheduler.HasRunningJob)
            {
                StatusText = string.Equals(_runningJobId, existing.Id, StringComparison.Ordinal)
                    ? "That job is already running."
                    : "A job is already running. Stop it first, or Add to queue for a duplicate.";
                return;
            }

            existing.Options = job.Options;
            ResumeStoredJob(existing);
            return;
        }

        if (job.Options.PackAsZip && job.Catcher is null && job.SourceKind != SourceKind.File)
        {
            var owner = Application.Current?.MainWindow;
            var proceed = MessageBox.Show(
                owner,
                job.Options.SkipCompressedWhenPacking
                    ? "Already-compressed files (video, photos, audio, zip/7z/rar, ISO) are copied as-is — they are not wrapped in the transport zip. If every file is already compressed, Mercury skips packing entirely.\n\nOther files are packed into a stored zip, then unpacked at the destination.\n\nContinue?"
                    : "Small Files will wrap every file in a stored transport zip, including video and archives.\n\nContinue?",
                "Small Files",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (proceed != MessageBoxResult.Yes)
            {
                return;
            }
        }

        job.ScheduledStart = null;
        ClearPreviousRunPresentation();
        TransferRundown.MarkStarted(job);
        ShowStartingStage(job);
        EnqueueJob(job, "Starting…", startNow: true);
    }

    private Job? FindMatchingResumable(Job form) =>
        FindMatchingResumable(
            form.SourcePath,
            form.Catcher is not null,
            form.Catcher?.TemplateId,
            form.DestinationPath);

    private Job? FindMatchingResumableForUi()
    {
        if (!HasPaths)
        {
            return null;
        }

        return FindMatchingResumable(
            PathNormalizer.Normalize(SourcePath),
            DestIsCatcher,
            SelectedCatcherTemplate?.Id,
            DestIsCatcher ? "" : PathNormalizer.Normalize(DestPath));
    }

    private Job? FindMatchingResumable(string source, bool isCatcher, string? catcherTemplateId, string dest)
    {
        Job? best = null;
        foreach (var queued in _scheduler.Queue)
        {
            if (!IsResumable(queued.Status) || queued.OnHold || !SameRoute(queued, source, isCatcher, catcherTemplateId, dest))
            {
                continue;
            }

            if (best is null)
            {
                best = queued;
                continue;
            }

            if ((queued.StartedUtc ?? DateTimeOffset.MinValue) >= (best.StartedUtc ?? DateTimeOffset.MinValue))
            {
                best = queued;
            }
        }

        if (best is not null)
        {
            return best;
        }

        var last = _lastJob ?? _scheduler.TryLoadLastJob();
        if (last is not null && IsResumable(last.Status) && SameRoute(last, source, isCatcher, catcherTemplateId, dest))
        {
            return last;
        }

        return null;
    }

    private static bool SameRoute(Job queued, string source, bool isCatcher, string? catcherTemplateId, string dest)
    {
        if (!string.Equals(queued.SourcePath, source, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (queued.Catcher is not null || isCatcher)
        {
            return isCatcher && string.Equals(queued.Catcher?.TemplateId, catcherTemplateId, StringComparison.Ordinal);
        }

        return string.Equals(queued.DestinationPath, dest, StringComparison.OrdinalIgnoreCase);
    }

    private void AddToQueue()
    {
        if (DestIsCatcher)
        {
            try
            {
                CatcherCrypto.ValidatePassphrase(_catcherPassphrase);
            }
            catch (Exception ex)
            {
                StatusText = ex.Message;
                return;
            }
        }

        var job = BuildJob();
        if (!ConfirmPurgeIfJobNeedsIt(job))
        {
            return;
        }

        EnqueueJob(
            job,
            job.ScheduledStart is { } start
                ? $"Queued “{job.Name}” to start after {start.LocalDateTime:ddd d MMM HH:mm}."
                : $"Queued “{job.Name}”.");
    }

    private void AddToQueueFromQueue()
    {
        EnsureQueueDraftSeeded();
        if (QueueDestIsCatcher)
        {
            try
            {
                CatcherCrypto.ValidatePassphrase(_catcherPassphrase);
            }
            catch (Exception ex)
            {
                StatusText = ex.Message;
                return;
            }
        }

        var job = BuildJob(
            QueueSourcePath,
            QueueDestPath,
            QueueDestKindIndex,
            QueueSelectedCatcherTemplate ?? QueueDraft.SelectedCatcherTemplate,
            QueueDraft.ToOptions(),
            QueueDraft.ToScheduledStart());
        if (!ConfirmPurgeIfJobNeedsIt(job))
        {
            return;
        }

        EnqueueJob(
            job,
            job.ScheduledStart is { } start
                ? $"Queued “{job.Name}” to start after {start.LocalDateTime:ddd d MMM HH:mm}."
                : $"Queued “{job.Name}”.");
    }

    private void ResumeLast()
    {
        var last = _scheduler.TryLoadLastJob();
        if (last is null)
        {
            StatusText = "No last job to resume.";
            return;
        }

        last.Options = BuildOptions();
        ResumeStoredJob(last);
    }

    private void ResumeStoredJob(Job last)
    {
        if (last is null)
        {
            return;
        }

        if (!ConfirmPurgeIfJobNeedsIt(last))
        {
            return;
        }

        last.SourcePath = string.IsNullOrWhiteSpace(SourcePath) ? last.SourcePath : SourcePath;
        last.DestinationPath = string.IsNullOrWhiteSpace(DestPath) ? last.DestinationPath : DestPath;
        if (last.Catcher is not null || DestIsCatcher)
        {
            try
            {
                CatcherCrypto.ValidatePassphrase(_catcherPassphrase);
                var templateId = last.Catcher?.TemplateId ?? SelectedCatcherTemplate?.Id;
                if (!string.IsNullOrWhiteSpace(templateId))
                {
                    CatcherSession.Remember(templateId, _catcherPassphrase);
                }
            }
            catch (Exception ex)
            {
                StatusText = ex.Message;
                return;
            }
        }

        bool? scan = false;
        if (JobJournal.Exists(_scheduler.Paths.JobDirectory(last.Id)))
        {
            scan = OfferSourceScan(last);
            if (scan is null)
            {
                StatusText = "Resume cancelled.";
                return;
            }
        }

        last.Status = JobStatus.Pending;
        last.ScheduledStart = null;
        last.ScanSourceOnResume = scan.Value;
        TransferRundown.MarkResumed(last);
        ShowStartingStage(last, resume: true);
        EnqueueJob(
            last,
            scan.Value ? "Resuming — checking source for changes…" : "Resuming last job…",
            startNow: true);
        RefreshFolderTree(force: true);
    }

    private static bool IsResumable(JobStatus status) =>
        status is not JobStatus.Completed;

    private void ApplyResumableSnapshot(Job last)
    {
        var snap = _scheduler.TrySnapshotProgress(last);
        if (snap is null)
        {
            return;
        }

        _lastProgress = snap;
        JobPercent = snap.Percent;
        CurrentFile = snap.CurrentFile ?? "";
        JobStats = ComposeStats(snap);
        OverallStats = ComposeStats(snap, includeStage: false);
        Rundown = TransferRundown.From(last);
        StatusText = "Resume to continue this job.";
        NotifyHeader();
    }

    private bool? OfferSourceScan(Job last)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
            ?? Application.Current?.MainWindow;
        var result = ChoiceWindow.Show(
            owner,
            "Resume last",
            string.IsNullOrWhiteSpace(last.SourcePath)
                ? "Check source for changes first?\n\nYes walks the source against the journal (new, changed, or gone files). Stop cancels that walk. No resumes the journal as-is."
                : $"Check source for changes first?\n\nYes walks {last.SourcePath} against the journal (new, changed, or gone files). Stop cancels that walk. No resumes the journal as-is.",
            "Yes",
            "No",
            "Cancel");
        return result switch
        {
            ChoiceResult.Primary => true,
            ChoiceResult.Secondary => false,
            _ => null
        };
    }

    private void ShowStartingStage(Job job, bool resume = false)
    {
        var catcher = job.Catcher is not null;
        var pack = catcher
            ? job.SourceKind != SourceKind.File
            : job.Options.PackAsZip && job.SourceKind != SourceKind.File;
        var stages = CopyPipeline.For(job, hasJournalFiles: resume, pack);
        var first = stages[0];
        var started = job.StartedUtc ?? DateTimeOffset.UtcNow;
        var preview = new JobProgress
        {
            JobId = job.Id,
            Status = JobStatus.Preparing,
            Message = first.Label + "…",
            StageIndex = 1,
            StageCount = stages.Count,
            StageName = first.Label,
            StartedUtc = started,
            StageStartedUtc = started
        };
        _lastProgress = preview;
        JobStats = ComposeStats(preview);
        OverallStats = ComposeStats(preview, includeStage: false);
        Rundown = TransferRundown.Live(job, DateTimeOffset.UtcNow);
        StatusText = first.Label + "…";
    }

    private void EnqueueJob(Job job, string status, bool startNow = false)
    {
        SaveBandwidth();
        RememberPath(isSource: true, job.SourcePath);
        if (job.Catcher is null)
        {
            RememberPath(isSource: false, job.DestinationPath);
        }
        _lastJob = job;
        _scheduler.Enqueue(job, startNow);
        StatusText = status;
        RefreshRunState();
    }

    private void Pause()
    {
        if (_runningJobId is null)
        {
            return;
        }

        _scheduler.Pause(_runningJobId);
        IsPaused = true;
        StatusText = "Paused.";
    }

    private bool CanPauseAfterThisFile() =>
        _runningJobId is not null && _scheduler.CanPauseAfterFile(_runningJobId);

    private void PauseAfterThisFile()
    {
        if (_runningJobId is null)
        {
            return;
        }

        if (_scheduler.IsPauseAfterFilePending(_runningJobId))
        {
            _closeAfterPause = false;
            _scheduler.CancelPauseAfterFile(_runningJobId);
            StatusText = "Pause after this file cancelled.";
            NotifyPauseAfter();
            if (_lastProgress is not null)
            {
                JobStats = ComposeStats(_lastProgress);
            }

            NotifyHeader();
            return;
        }

        var eta = _scheduler.EstimateCurrentFileEta(_runningJobId);
        if (eta is null || eta.Value.TotalSeconds >= 10)
        {
            var wait = eta is null
                ? "until the current file finishes"
                : ByteFormatter.AboutDuration(eta.Value);
            var owner = Application.Current?.MainWindow;
            MessageBox.Show(
                owner,
                "Mercury will pause after the current file finishes.\nEstimated wait: " + wait + ".",
                "Pause after this file",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        _scheduler.RequestPauseAfterFile(_runningJobId);
        StatusText = "Pause after this file requested.";
        NotifyPauseAfter();
        if (_lastProgress is not null)
        {
            JobStats = ComposeStats(_lastProgress);
        }

        NotifyHeader();
    }

    private void NotifyPauseAfter()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PauseAfterFileArmed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PauseAfterThisFileLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PauseAfterThisFileToolTip)));
        (PauseAfterThisFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void PauseAll()
    {
        _scheduler.PauseAll();
        IsGlobalPaused = true;
        StatusText = "Paused all jobs.";
    }

    private void ResumeAll()
    {
        _scheduler.ResumeAll();
        IsGlobalPaused = false;
        if (!IsPaused)
        {
            StatusText = "Copying…";
        }
    }

    private void ResumePaused()
    {
        if (_runningJobId is null)
        {
            return;
        }

        _scheduler.ResumePaused(_runningJobId);
        IsPaused = false;
        StatusText = "Copying…";
    }

    private void Stop()
    {
        if (_runningJobId is not null)
        {
            _scheduler.Stop(_runningJobId);
        }

        IsGlobalPaused = false;
        StatusText = "Stopping…";
        RefreshRunState();
    }

    private Job BuildJob(
        string? sourcePath = null,
        string? destPath = null,
        int? destKindIndex = null,
        CatcherEnvelope? catcherTemplate = null,
        JobOptions? options = null,
        DateTimeOffset? scheduledStart = null)
    {
        var source = PathNormalizer.Normalize(sourcePath ?? SourcePath);
        var catcherDest = (destKindIndex ?? DestKindIndex) == 1;
        var builtOptions = options ?? BuildOptions();
        var start = scheduledStart ?? (options is null ? BuildScheduledStart() : scheduledStart);
        if (catcherDest)
        {
            var template = catcherTemplate ?? SelectedCatcherTemplate
                ?? throw new InvalidOperationException("Pick a Catcher template on the Transfer, Queue, or Network tab.");
            CatcherCrypto.ValidatePassphrase(_catcherPassphrase);
            CatcherSession.Remember(template.Id, _catcherPassphrase);
            return new Job
            {
                SourcePath = source,
                DestinationPath = CatcherCrypto.FormatDestination(template.PublicHost, template.PublicPort, template.Name),
                SourceKind = CopyShape.DetectKind(source),
                Catcher = template.ToTarget(),
                Options = builtOptions,
                VolumeSerial = VolumeInfo.GetSerial(source),
                ScheduledStart = start
            };
        }

        var dest = PathNormalizer.Normalize(destPath ?? DestPath);
        return new Job
        {
            SourcePath = source,
            DestinationPath = dest,
            SourceKind = CopyShape.DetectKind(source),
            Options = builtOptions,
            VolumeSerial = VolumeInfo.GetSerial(source),
            ScheduledStart = start
        };
    }

    private DateTimeOffset? BuildScheduledStart()
    {
        if (!ScheduleEnabled || ScheduledDate is not { } date || !TimeOnly.TryParse(ScheduledTime, out var time))
        {
            return null;
        }

        var local = DateTime.SpecifyKind(date.Date.Add(time.ToTimeSpan()), DateTimeKind.Local);
        return new DateTimeOffset(local);
    }

    private JobOptions BuildOptions()
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

    private static bool ConfirmPurge(string leading)
    {
        var owner = Application.Current?.MainWindow;
        var proceed = MessageBox.Show(
            owner,
            leading + "\n\nFiles and folders at the destination that are not in the source will be deleted. This cannot be undone.",
            "RoboFlags — Purge",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return proceed == MessageBoxResult.Yes;
    }

    private static bool ConfirmPurgeIfJobNeedsIt(Job job)
    {
        if (!job.Options.PurgeExtraDestFiles || job.Catcher is not null)
        {
            return true;
        }

        return ConfirmPurge("This job has Purge extra dest files on.");
    }

    private void ApplyJobOptions(JobOptions options)
    {
        _applyingJobOptions = true;
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
        }
        finally
        {
            _applyingJobOptions = false;
        }
    }

    private void SaveBandwidth()
    {
        double? min = null;
        if (BandwidthUnit.TryParseMegabytes(GlobalMinText, ShowSpeedInMegabits, out var minMb))
        {
            min = minMb;
        }

        double? max = null;
        if (!GlobalMaxUnlimited && BandwidthUnit.TryParseMegabytes(GlobalMaxText, ShowSpeedInMegabits, out var maxMb))
        {
            max = maxMb;
        }

        double? idle = null;
        if (IdleThrottleEnabled && BandwidthUnit.TryParseMegabytes(IdleThrottleText, ShowSpeedInMegabits, out var idleMb))
        {
            idle = idleMb;
        }

        var settings = new BandwidthSettings
        {
            GlobalMinMegabytesPerSecond = min,
            GlobalMaxMegabytesPerSecond = max,
            IdleThrottleMegabytesPerSecond = idle,
            IdleCpuPercentThreshold = MachineLoadSampler.DefaultBusyPercent,
            ShowSpeedInMegabits = ShowSpeedInMegabits
        };
        AppSettingsStore.Save(_paths, settings);
        _scheduler.Budget.Apply(settings);
    }

    // Phase 3: hours, overwrite, verify, retries, dest path, etc. should later apply
    // in-flight with warning popups for dangerous changes. Speed is live now.
    private void ApplyLiveSpeed()
    {
        SaveBandwidth();
        var running = _scheduler.TryGetRunningJob();
        if (running is not null)
        {
            WriteSpeedToJob(running, UnlimitedSpeed, MaxMBpsText);
        }
    }

    private void ApplyQueueJobSpeed(QueueJobItem item)
    {
        WriteSpeedToJob(item.Job, item.UnlimitedSpeed, item.MaxMBpsText, displayUnits: false);
        item.RaiseComputed();
    }

    private void WriteSpeedToJob(Job job, bool unlimited, string maxText, bool displayUnits = true)
    {
        double? max = null;
        if (!unlimited)
        {
            if (displayUnits)
            {
                if (!BandwidthUnit.TryParseMegabytes(maxText, ShowSpeedInMegabits, out var mb))
                {
                    return;
                }

                max = mb;
            }
            else if (!double.TryParse(maxText, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) || mb <= 0)
            {
                return;
            }
            else
            {
                max = mb;
            }
        }

        job.Options.MaxMegabytesPerSecond = max;
        _scheduler.PersistJob(job);
    }

    private void ReloadCatcherTemplates()
    {
        var selectedId = SelectedCatcherTemplate?.Id;
        CatcherTemplates.Clear();
        foreach (var template in CatcherStore.LoadTemplates(_paths))
        {
            CatcherTemplates.Add(template);
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCatcherTemplates)));
        if (selectedId is not null)
        {
            SelectedCatcherTemplate = CatcherTemplates.FirstOrDefault(t => t.Id == selectedId);
        }
        else if (CatcherTemplates.Count == 1)
        {
            SelectedCatcherTemplate = CatcherTemplates[0];
        }
    }

    private void CreateCatcherTemplate()
    {
        try
        {
            CatcherCrypto.ValidatePassphrase(_catcherPassphrase);
            if (!int.TryParse(NetworkPublicPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var publicPort))
            {
                publicPort = CatcherScheme.DefaultPublicPort;
            }

            if (!int.TryParse(NetworkInternalPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var internalPort))
            {
                internalPort = CatcherScheme.DefaultInternalPort;
            }

            var envelope = CatcherPack.Create(
                NetworkName,
                NetworkPublicHost,
                publicPort,
                internalPort,
                NetworkBindAddress,
                _catcherPassphrase);
            CatcherStore.Upsert(_paths, envelope);
            ReloadCatcherTemplates();
            SelectedCatcherTemplate = CatcherTemplates.FirstOrDefault(t => t.Id == envelope.Id);
            NetworkFingerprint = envelope.FingerprintSha256;
            NetworkHint = envelope.PortForwardHint();
            NetworkStatus = $"Created “{envelope.Name}”. Export the file and import it on Catcher. Fingerprint {envelope.FingerprintSha256}";
            if (CatcherCrypto.IsLoopbackOnly(envelope.BindAddress) ||
                envelope.PublicHost is "127.0.0.1" or "localhost" or "::1")
            {
                NetworkStatus += " This host/bind is loopback — it only works on this PC, not across a router.";
            }
        }
        catch (Exception ex)
        {
            NetworkStatus = ex.Message;
        }
    }

    private void ExportCatcherTemplate()
    {
        if (SelectedCatcherTemplate is null)
        {
            NetworkStatus = "Select or create a template first.";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Export Catcher file",
            Filter = "Catcher pack (*.mercury-catch)|*.mercury-catch",
            FileName = CatcherCrypto.SafeFileName(SelectedCatcherTemplate.Name) + CatcherScheme.FileExtension
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            CatcherPack.ExportFile(SelectedCatcherTemplate, dlg.FileName);
            var readBack = CatcherPack.LoadFile(dlg.FileName);
            NetworkStatus = $"Exported {dlg.FileName} (fingerprint {readBack.FingerprintSha256}). Copy this file to the Catcher PC. The passphrase is not stored in the file.";
        }
        catch (Exception ex)
        {
            NetworkStatus = ex.Message;
        }
    }

    private void ImportCatcherTemplate()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import Catcher file",
            Filter = "Catcher pack (*.mercury-catch)|*.mercury-catch|JSON (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var envelope = CatcherPack.LoadFile(dlg.FileName);
            CatcherStore.Upsert(_paths, envelope);
            ReloadCatcherTemplates();
            SelectedCatcherTemplate = CatcherTemplates.FirstOrDefault(t => t.Id == envelope.Id);
            NetworkStatus = $"Imported “{envelope.Name}”. Fingerprint {envelope.FingerprintSha256}";
        }
        catch (Exception ex)
        {
            NetworkStatus = ex.Message;
        }
    }

    private void DeleteCatcherTemplate()
    {
        if (SelectedCatcherTemplate is null)
        {
            return;
        }

        CatcherStore.Remove(_paths, SelectedCatcherTemplate.Id);
        CatcherSession.Forget(SelectedCatcherTemplate.Id);
        SelectedCatcherTemplate = null;
        ReloadCatcherTemplates();
        NetworkStatus = "Template deleted.";
    }

    private void BrowseSource()
    {
        var path = BrowseAny("Choose source", isSource: true, current: SourcePath);
        if (path is not null)
        {
            SourcePath = path;
            RememberPath(isSource: true, path);
        }
    }

    private void BrowseDest()
    {
        var path = BrowseAny("Choose destination", isSource: false, current: DestPath);
        if (path is not null)
        {
            DestPath = path;
            RememberPath(isSource: false, path);
        }
    }

    private void BrowseQueueSource()
    {
        var path = BrowseAny("Choose source", isSource: true, current: QueueSourcePath);
        if (path is not null)
        {
            QueueSourcePath = path;
            RememberPath(isSource: true, path);
        }
    }

    private void BrowseQueueDest()
    {
        var path = BrowseAny("Choose destination", isSource: false, current: QueueDestPath);
        if (path is not null)
        {
            QueueDestPath = path;
            RememberPath(isSource: false, path);
        }
    }

    public void PrepareQueueForm()
    {
        QueueSourcePath = SourcePath;
        QueueDestPath = DestPath;
        QueueDestKindIndex = DestKindIndex;
        QueueSelectedCatcherTemplate = SelectedCatcherTemplate;
        EnsureQueueDraftSeeded();
        QueueDraft.CatcherTemplates = CatcherTemplates;
        QueueDraft.ShowCatcherTemplate = QueueDestIsCatcher;
        QueueDraft.SelectedCatcherTemplate = QueueSelectedCatcherTemplate;
        QueueDraft.SetLandingPaths(QueueSourcePath, QueueDestPath);
        QueueDraft.RaiseAddCanExecute();
        RaiseQueueDraftBadges();
    }

    private void EnsureQueueDraftSeeded()
    {
        if (_queueDraftSeeded)
        {
            QueueDraft.CatcherTemplates = CatcherTemplates;
            QueueDraft.ShowCatcherTemplate = QueueDestIsCatcher;
            return;
        }

        QueueDraft.LoadFrom(BuildOptions(), BuildScheduledStart());
        QueueDraft.PrepareDraft(
            CatcherTemplates,
            QueueDestIsCatcher,
            QueueSelectedCatcherTemplate,
            QueueSourcePath,
            QueueDestPath);
        _queueDraftSeeded = true;
        RaiseQueueDraftBadges();
    }

    private void OpenQueueDraftOptions()
    {
        EnsureQueueDraftSeeded();
        QueueDraft.ShowSpeedInMegabits = ShowSpeedInMegabits;
        QueueDraft.PrepareDraft(
            CatcherTemplates,
            QueueDestIsCatcher,
            QueueSelectedCatcherTemplate,
            QueueSourcePath,
            QueueDestPath);
        OpenJobOptionsRequested?.Invoke(QueueDraft);
    }

    private void OpenQueueJobOptions(QueueJobItem? item)
    {
        if (item is null)
        {
            return;
        }

        SelectedQueueJob = item;
        _jobFormItem = item;
        _jobForm ??= new JobOptionsForm { ConfirmPurge = ConfirmPurge };
        _jobForm.ShowSpeedInMegabits = ShowSpeedInMegabits;
        _jobForm.Applied -= OnJobFormApplied;
        _jobForm.SpeedChanged -= OnJobFormSpeed;
        _jobForm.LoadFrom(item.Job, CatcherTemplates);
        _jobForm.Applied += OnJobFormApplied;
        _jobForm.SpeedChanged += OnJobFormSpeed;
        OpenJobOptionsRequested?.Invoke(_jobForm);
    }

    private void OnJobFormApplied()
    {
        if (_jobForm is null || _jobFormItem is null)
        {
            return;
        }

        _jobForm.WriteTo(_jobFormItem.Job);
        _scheduler.PersistJob(_jobFormItem.Job);
        _jobFormItem.SyncSpeedFromJob();
        _jobFormItem.RaiseComputed();
        RaiseQueueDraftBadges();
    }

    private void OnJobFormSpeed()
    {
        if (_jobForm is null || _jobFormItem is null)
        {
            return;
        }

        WriteSpeedToJob(_jobFormItem.Job, _jobForm.UnlimitedSpeed, _jobForm.MaxMBpsText);
        _jobFormItem.SyncSpeedFromJob();
        _jobFormItem.RaiseComputed();
    }

    private void OnQueueDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JobOptionsForm.SelectedCatcherTemplate))
        {
            _suppressQueueDraftSync = true;
            try
            {
                QueueSelectedCatcherTemplate = QueueDraft.SelectedCatcherTemplate;
            }
            finally
            {
                _suppressQueueDraftSync = false;
            }
        }

        RaiseQueueDraftBadges();
        QueueDraft.RaiseAddCanExecute();
    }

    private void RaiseQueueDraftBadges()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueDraftBadges)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasQueueDraftBadges)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueLandingPreview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowQueueLandingPreview)));
    }

    private string? BrowseAny(string title, bool isSource, string? current)
    {
        var recents = LibraryStore.LoadRecents(_paths);
        var start = LibraryStore.BrowseStartDir(isSource, recents, current ?? (isSource ? SourcePath : DestPath));
        var folder = new OpenFolderDialog { Title = title + " — folder or drive" };
        if (!string.IsNullOrEmpty(start))
        {
            folder.InitialDirectory = start;
        }

        if (folder.ShowDialog() == true)
        {
            return folder.FolderName;
        }

        var file = new OpenFileDialog
        {
            Title = title + " — file",
            CheckFileExists = true,
            Multiselect = false
        };
        if (!string.IsNullOrEmpty(start))
        {
            file.InitialDirectory = start;
        }

        return file.ShowDialog() == true ? file.FileName : null;
    }

    private void RememberPath(bool isSource, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var recents = LibraryStore.LoadRecents(_paths);
        if (isSource)
        {
            LibraryStore.Remember(recents.Sources, path);
            LibraryStore.RememberBrowseDir(recents, isSource: true, path);
        }
        else
        {
            LibraryStore.Remember(recents.Destinations, path);
            LibraryStore.RememberBrowseDir(recents, isSource: false, path);
        }

        LibraryStore.SaveRecents(_paths, recents);
        Replace(RecentSources, recents.Sources);
        Replace(RecentDestinations, recents.Destinations);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRecentSources)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRecentDestinations)));
    }

    private void ReloadLibrary()
    {
        var recents = LibraryStore.LoadRecents(_paths);
        Replace(RecentSources, recents.Sources);
        Replace(RecentDestinations, recents.Destinations);
        Replace(SavedJobs, LibraryStore.LoadSavedJobs(_paths));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRecentSources)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRecentDestinations)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSavedJobs)));
    }

    private void SaveCurrentJob()
    {
        var owner = Application.Current?.MainWindow;
        if (owner is null)
        {
            return;
        }

        var suggested = Path.GetFileName(SourcePath.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(suggested))
        {
            suggested = "Saved job";
        }

        var name = PromptWindow.Ask(owner, "Save job", "Name this transfer template:", suggested);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var existing = SavedJobs.FirstOrDefault(j => string.Equals(j.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.SourcePath = SourcePath;
            existing.DestinationPath = DestPath;
            existing.Options = BuildOptions();
        }
        else
        {
            SavedJobs.Add(new SavedJob
            {
                Name = name,
                SourcePath = SourcePath,
                DestinationPath = DestPath,
                Options = BuildOptions()
            });
        }

        LibraryStore.SaveSavedJobs(_paths, SavedJobs.ToList());
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSavedJobs)));
        StatusText = $"Saved job “{name}”.";
    }

    private void LoadSavedJob(SavedJob? saved)
    {
        if (saved is null)
        {
            return;
        }

        SourcePath = saved.SourcePath;
        DestPath = saved.DestinationPath;
        ApplyJobOptions(saved.Options);
        StatusText = $"Loaded saved job “{saved.Name}”.";
    }

    private void DeleteSavedJob(SavedJob? saved)
    {
        if (saved is null)
        {
            return;
        }

        SavedJobs.Remove(saved);
        LibraryStore.SaveSavedJobs(_paths, SavedJobs.ToList());
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSavedJobs)));
        StatusText = $"Removed saved job “{saved.Name}”.";
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var next = items as IList<T> ?? items.ToList();
        var comparer = EqualityComparer<T>.Default;
        var same = target.Count == next.Count;
        if (same)
        {
            for (var i = 0; i < next.Count; i++)
            {
                if (!comparer.Equals(target[i], next[i]))
                {
                    same = false;
                    break;
                }
            }
        }

        if (same)
        {
            return;
        }

        for (var i = 0; i < next.Count; i++)
        {
            if (i < target.Count)
            {
                if (!comparer.Equals(target[i], next[i]))
                {
                    target[i] = next[i];
                }
            }
            else
            {
                target.Add(next[i]);
            }
        }

        while (target.Count > next.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private void OpenLogs()
    {
        Directory.CreateDirectory(_paths.Logs);
        Process.Start(new ProcessStartInfo
        {
            FileName = _paths.Logs,
            UseShellExecute = true
        });
    }

    private void CopyConsole()
    {
        var text = string.Join(Environment.NewLine, ConsoleLines.Select(l => l.Text));
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
        }
    }

    private void FollowLatest()
    {
        FollowConsole = true;
    }

    private bool MatchesConsole(object obj)
    {
        if (obj is not ConsoleLine line)
        {
            return false;
        }

        if (ErrorsOnly && !line.IsError)
        {
            return false;
        }

        if (string.IsNullOrEmpty(ConsoleSearch))
        {
            return true;
        }

        return line.Text.Contains(ConsoleSearch, StringComparison.OrdinalIgnoreCase);
    }

    private void OnProgress(object? sender, JobProgress e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ApplyProgress(e);
            return;
        }

        if (dispatcher.CheckAccess())
        {
            ApplyProgress(e);
        }
        else
        {
            dispatcher.Invoke(() => ApplyProgress(e));
        }
    }

    private void ApplyProgress(JobProgress e)
    {
        if (!string.IsNullOrEmpty(e.JobId) && e.JobId != _progressJobId)
        {
            var incomingLive = e.Status is JobStatus.Preparing or JobStatus.Enumerating or JobStatus.Copying
                or JobStatus.Verifying or JobStatus.Paused or JobStatus.PausedOutsideHours;
            if (incomingLive)
            {
                ResultBanner = "";
                ResultBrush = Brushes.Transparent;
            }

            _progressJobId = e.JobId;
        }

        _lastProgress = e;
        if (e.IsRundownStage && _scheduler.BlocksStart)
        {
            var copy = _scheduler.TryGetRunningJob();
            if (copy is not null && copy.Id != e.JobId)
            {
                var rundownRow = QueueJobs.FirstOrDefault(j => j.Job.Id == e.JobId);
                rundownRow?.ApplyProgress(e);
                RefreshRunState();
                return;
            }
        }

        JobStats = ComposeStats(e);
        var overall = _scheduler.GetOverallProgress();
        if (e.BytesTotal > 0 || e.FilesTotal > 0 || e.Percent > JobPercent)
        {
            JobPercent = e.Percent;
        }

        OverallPercent = ProgressHeader.ShowOverall(QueueJobs.Count) ? overall.Percent : JobPercent;
        CurrentFile = e.CurrentFile ?? CurrentFile;
        StatusText = ProgressHeader.HeaderStatus(e.Message, e.Status);
        OverallStats = ComposeStats(overall, includeStage: false);
        CloudWarning = e.CloudDestination;
        if (_closeAfterPause && e.Status == JobStatus.Paused)
        {
            _closeAfterPause = false;
            _keepHeartbeatOnClose = true;
            CloseWindowRequested?.Invoke();
            return;
        }
        if (e.IsRundownStage)
        {
            var running = _scheduler.TryGetRunningJob(e.JobId) ?? _scheduler.TryGetRundownJob(e.JobId)
                          ?? _scheduler.TryGetRunningJob() ?? _scheduler.TryGetRundownJob();
            if (running is not null)
            {
                Rundown = TransferRundown.From(running);
            }

            EnsureElapsedTimer();
            _elapsedTimer?.Start();
        }
        else if (e.Status is JobStatus.Completed or JobStatus.Incomplete or JobStatus.Cancelled or JobStatus.Failed)
        {
            _elapsedTimer?.Stop();
            var finished = QueueJobs.FirstOrDefault(j => j.Job.Id == e.JobId)?.Job
                         ?? _scheduler.Queue.FirstOrDefault(j => j.Id == e.JobId);
            if (finished is not null)
            {
                ApplyResult(finished);
            }

            RefreshResume();
            ReloadHistory();
        }
        else
        {
            var running = _scheduler.TryGetRunningJob(e.JobId) ?? _scheduler.TryGetRunningJob();
            if (running is not null)
            {
                Rundown = TransferRundown.From(running);
            }

            EnsureElapsedTimer();
            _elapsedTimer?.Start();
        }

        var row = QueueJobs.FirstOrDefault(j => j.Job.Id == e.JobId);
        row?.ApplyProgress(e);
        NotifyHeader();
        RefreshRunState();
        NotifyPauseAfter();
        RefreshFolderTree(force: !e.IsRundownStage && e.Status is JobStatus.Completed or JobStatus.Incomplete or JobStatus.Cancelled or JobStatus.Failed);
    }

    private void EnsureCatcherProbe()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (_catcherProbeTimer is null)
        {
            _catcherProbeTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(4)
            };
            _catcherProbeTimer.Tick += OnCatcherProbeTick;
        }

        if (DestIsCatcher && SelectedCatcherTemplate is not null)
        {
            _catcherProbeTimer.Start();
            _ = ProbeCatcherAsync();
            return;
        }

        _catcherProbeTimer.Stop();
        CatcherOnlineStatus = "";
    }

    private void OnCatcherProbeTick(object? sender, EventArgs e) => _ = ProbeCatcherAsync();

    private async Task ProbeCatcherAsync()
    {
        if (!DestIsCatcher || SelectedCatcherTemplate is null)
        {
            CatcherOnlineStatus = "";
            return;
        }

        if (string.IsNullOrEmpty(_catcherPassphrase) || _catcherPassphrase.Length < CatcherScheme.MinPassphraseLength)
        {
            CatcherOnlineStatus = "Catcher: enter the template passphrase (required on every connect). Status unknown until then.";
            return;
        }

        var target = SelectedCatcherTemplate.ToTarget();
        CatcherProbeResult result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            result = await CatcherClient.ProbeAsync(target, _catcherPassphrase, cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = new CatcherProbeResult(Mercury.CatcherOnlineStatus.Offline, "Catcher is offline or unreachable.");
        }

        void apply()
        {
            CatcherOnlineStatus = result.Status switch
            {
                Mercury.CatcherOnlineStatus.Ready => "Catcher: Ready",
                Mercury.CatcherOnlineStatus.Listening => "Catcher: Listening (tick Ready to receive on the Catcher)",
                Mercury.CatcherOnlineStatus.Unauthorized => "Catcher: passphrase rejected",
                Mercury.CatcherOnlineStatus.FingerprintMismatch => "Catcher: TLS fingerprint mismatch — refusing to connect",
                _ => "Catcher: Offline"
            };
            if (!string.IsNullOrWhiteSpace(result.Message) && result.Status is not Mercury.CatcherOnlineStatus.Ready)
            {
                CatcherOnlineStatus += " — " + result.Message;
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            apply();
        }
        else
        {
            dispatcher.Invoke(apply);
        }
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SyncQueue(rebuild: true);
            RefreshRunState();
            return;
        }

        dispatcher.Invoke(() =>
        {
            SyncQueue(rebuild: true);
            RefreshRunState();
        });
    }

    private void SyncQueue(bool rebuild)
    {
        if (!rebuild)
        {
            foreach (var item in QueueJobs)
            {
                var progress = _scheduler.GetProgress(item.Job.Id);
                if (progress is not null)
                {
                    item.ApplyProgress(progress);
                }
                else
                {
                    item.RaiseComputed();
                }
            }

            return;
        }

        var selectedId = SelectedQueueJob?.Job.Id;
        QueueJobs.Clear();
        var jobs = _scheduler.Queue;
        for (var i = 0; i < jobs.Count; i++)
        {
            var item = new QueueJobItem(jobs[i], ApplyQueueJobSpeed)
            {
                Order = i + 1,
                CanMoveUp = i > 0,
                CanMoveDown = i < jobs.Count - 1
            };
            var progress = _scheduler.GetProgress(jobs[i].Id);
            if (progress is not null)
            {
                item.ApplyProgress(progress);
            }

            QueueJobs.Add(item);
        }

        SelectedQueueJob = selectedId is null
            ? null
            : QueueJobs.FirstOrDefault(j => j.Job.Id == selectedId);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasQueueJobs)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowOverallProgress)));
        NotifyHeader();
    }

    private void NotifyHeader()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowOverallProgress)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderStats)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderRundown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderRundownLine)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowHeaderRundown)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderElapsedText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowHeaderElapsed)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HeaderEtaText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowHeaderEta)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowHeaderStatus)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowProgressDetail)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(JobPercent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OverallPercent)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(JobPercentText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OverallPercentText)));
    }

    private ProgressStats ComposeStats(JobProgress current, bool includeStage = true)
    {
        var (index, count) = _scheduler.QueueOrdinal(string.IsNullOrEmpty(current.JobId) ? _runningJobId : current.JobId);
        JobProgress? overall = ProgressHeader.ShowOverall(QueueJobs.Count) ? _scheduler.GetOverallProgress() : null;
        return ProgressStats.From(
            current,
            includeStage: includeStage,
            overall: overall,
            jobIndex: index,
            jobCount: count,
            pauseAfter: PauseAfterPair(),
            megabits: ShowSpeedInMegabits);
    }

    private StatPair PauseAfterPair()
    {
        if (_runningJobId is null || !_scheduler.IsPauseAfterFilePending(_runningJobId))
        {
            return StatPair.Empty;
        }

        var eta = _scheduler.EstimateCurrentFileEta(_runningJobId);
        var wait = eta is null || eta.Value <= TimeSpan.Zero
            ? "until this file finishes"
            : ByteFormatter.CompactEta(eta.Value);
        return new StatPair("Pause after this file", wait);
    }

    private void RefreshRunState()
    {
        var running = _scheduler.TryGetRunningJob();
        var rundown = running is null ? _scheduler.TryGetRundownJob() : null;
        _runningJobId = running?.Id ?? rundown?.Id;
        IsRunning = _scheduler.BlocksStart;
        HasBackgroundRundown = _scheduler.HasBackgroundRundown;
        IsPaused = running is { Status: JobStatus.Paused };
        IsGlobalPaused = _scheduler.GlobalPause.IsPaused;
        RaiseRunCommands();
        NotifyHeader();
    }

    private void PauseQueueJob(QueueJobItem? item)
    {
        if (item is null || !item.CanPause)
        {
            return;
        }

        _scheduler.Pause(item.Job.Id);
        item.RaiseComputed();
        RefreshRunState();
    }

    private void ResumeQueueJob(QueueJobItem? item)
    {
        if (item is null || !item.CanResume)
        {
            return;
        }

        _scheduler.ResumeOrRetry(item.Job.Id);
        item.RaiseComputed();
        RefreshRunState();
    }

    private void StopQueueJob(QueueJobItem? item)
    {
        if (item is null || !item.CanStop)
        {
            return;
        }

        _scheduler.Stop(item.Job.Id);
        StatusText = "Stopping…";
        RefreshRunState();
    }

    private void RemoveQueueJob(QueueJobItem? item)
    {
        if (item is null)
        {
            return;
        }

        _scheduler.Remove(item.Job.Id);
        RefreshRunState();
    }

    private void MoveQueueJob(QueueJobItem? item, int delta)
    {
        if (item is null)
        {
            return;
        }

        _scheduler.Move(item.Job.Id, delta);
    }

    private void ToggleHoldQueueJob(QueueJobItem? item)
    {
        if (item is null || !item.CanToggleHold)
        {
            return;
        }

        _scheduler.SetHold(item.Job.Id, !item.Job.OnHold);
        item.RaiseComputed();
        RefreshRunState();
    }

    private void OnLog(LogEvent ev)
    {
        var dispatcher = Application.Current?.Dispatcher;
        void add()
        {
            AppendConsole($"{TransferRundown.FormatLogTime(ev.Utc)} [{ev.JobName}] {ev.Message}", ev.Level == "Error");
        }

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            add();
        }
        else
        {
            dispatcher.Invoke(add);
        }
    }

    private void AppendConsole(string text, bool error)
    {
        ConsoleLines.Add(new ConsoleLine { Text = text, IsError = error });
        while (ConsoleLines.Count > 5000)
        {
            ConsoleLines.RemoveAt(0);
        }
    }

    private void OnHistoryChanged(object? sender, EventArgs e) =>
        Dispatch(ReloadHistory);

    private void ReloadHistory()
    {
        var selectedId = SelectedHistoryItem?.Entry.Id;
        HistoryItems.Clear();
        foreach (var entry in HistoryStore.Load(_paths))
        {
            HistoryItems.Add(new HistoryItem(entry));
        }

        SelectedHistoryItem = selectedId is null
            ? null
            : HistoryItems.FirstOrDefault(h => h.Entry.Id == selectedId);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasHistoryItems)));
    }

    private void ClearPreviousRunPresentation()
    {
        _lastProgress = null;
        _progressJobId = null;
        Rundown = TransferRundown.Empty;
        ResultBanner = "";
        ResultBrush = Brushes.Transparent;
        JobPercent = 0;
        OverallPercent = 0;
        JobStats = ProgressStats.Idle;
        OverallStats = ProgressStats.Idle;
        CurrentFile = "";
        FolderTree.Clear();
        FolderTreeEmpty = true;
        EnsureElapsedTimer();
        _elapsedTimer?.Start();
    }

    private void EnsureElapsedTimer()
    {
        if (_elapsedTimer is not null)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _elapsedTimer.Tick += OnElapsedTick;
    }

    private void OnElapsedTick(object? sender, EventArgs e)
    {
        if (!IsRunning)
        {
            _elapsedTimer?.Stop();
            return;
        }

        if (IsPaused)
        {
            return;
        }

        var running = _scheduler.TryGetRunningJob(_runningJobId) ?? _scheduler.TryGetRunningJob();
        if (running is not null)
        {
            Rundown = TransferRundown.From(running);
        }

        if (_lastProgress is not null)
        {
            JobStats = ComposeStats(_lastProgress);
            OverallStats = ComposeStats(_scheduler.GetOverallProgress(), includeStage: false);
        }
        else if (running is { StartedUtc: not null })
        {
            var pack = running.Catcher is not null
                ? running.SourceKind != SourceKind.File
                : running.Options.PackAsZip && running.SourceKind != SourceKind.File;
            var preview = CopyPipeline.For(running, hasJournalFiles: false, pack);
            var first = preview[0];
            var stats = ComposeStats(new JobProgress
            {
                JobId = running.Id,
                Status = running.Status,
                StageIndex = 1,
                StageCount = preview.Count,
                StageName = first.Label,
                StartedUtc = running.StartedUtc,
                StageStartedUtc = running.StartedUtc
            });
            OverallStats = stats;
            JobStats = stats;
        }

        NotifyHeader();
        NotifyPauseAfter();
        RefreshFolderTree();
    }

    private void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    private void ApplyResult(Job job)
    {
        ResultBanner = ProgressHeader.HeaderResult(job);
        ResultBrush = job.Status switch
        {
            JobStatus.Completed => (Brush)Application.Current.FindResource("OkBrush"),
            JobStatus.Incomplete or JobStatus.Failed => (Brush)Application.Current.FindResource("DangerBrush"),
            JobStatus.Cancelled => (Brush)Application.Current.FindResource("WarnBrush"),
            _ => (Brush)Application.Current.FindResource("MutedBrush")
        };
        StatusText = ResultBanner;
    }

    private void ApplyRundown(Job job)
    {
        Rundown = TransferRundown.From(job);
    }

    private void RefreshResume()
    {
        _lastJob = _scheduler.TryLoadLastJob();
        CanResumeLast = _lastJob is { Status: not JobStatus.Completed };
        (ResumeLastCommand as RelayCommand)?.RaiseCanExecuteChanged();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartButtonLabel)));
    }

    private void RefreshFolderTree(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _folderTreeRefreshUtc < TimeSpan.FromSeconds(1.5))
        {
            return;
        }

        _folderTreeRefreshUtc = now;
        var jobId = _runningJobId ?? _lastProgress?.JobId ?? _lastJob?.Id;
        if (!string.Equals(jobId, _folderTreeJobId, StringComparison.OrdinalIgnoreCase))
        {
            _expandedFolders.Clear();
            _folderTreeJobId = jobId;
        }
        else
        {
            _expandedFolders.Clear();
            FolderTreeItem.CollectExpanded(FolderTree, _expandedFolders);
        }

        IReadOnlyList<FileRecord> files;
        try
        {
            files = _scheduler.LoadJournalFiles(jobId);
        }
        catch
        {
            files = [];
        }

        if (files.Count == 0)
        {
            FolderTree.Clear();
            FolderTreeEmpty = true;
            return;
        }

        var bps = 0d;
        if (_lastProgress is { } progress)
        {
            var elapsed = progress.StartedUtc is { } started
                ? DateTimeOffset.UtcNow - started
                : TimeSpan.Zero;
            bps = ByteFormatter.EffectiveRate(progress.BytesPerSecond, progress.BytesCopied, elapsed);
        }

        var sourceName = Path.GetFileName((_lastJob?.SourcePath ?? SourcePath).TrimEnd('\\', '/'));
        var nodes = Mercury.FolderTree.Build(files, bps, string.IsNullOrEmpty(sourceName) ? null : sourceName);
        FolderTree.Clear();
        foreach (var node in nodes)
        {
            FolderTree.Add(new FolderTreeItem(node, _expandedFolders));
        }

        FolderTreeEmpty = FolderTree.Count == 0;
    }

    private void RaiseLandingPreview()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LandingPreview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowLandingPreview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IncludeSourceFolderNameApplies)));
    }

    private void RaiseRunCommands()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartButtonLabel)));
        (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddToQueueCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (QueueAddToQueueCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PauseAfterThisFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResumePausedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PauseAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResumeAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResumeLastCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveJobCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PauseQueueJobCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResumeQueueJobCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopQueueJobCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RemoveQueueJobCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveQueueJobUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveQueueJobDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportCatcherCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCatcherCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseSourceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseDestCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseQueueSourceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseQueueDestCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenSelectedQueueJobOptionsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        QueueDraft.RaiseAddCanExecute();
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
