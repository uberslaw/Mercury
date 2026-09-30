using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Mercury;

public sealed class CompareViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppPaths _paths;
    private readonly JobScheduler? _scheduler;
    private readonly Dispatcher _dispatcher;
    private string? _activeJobId;
    private readonly PauseGate _pause = new();
    private readonly ManualResetEventSlim _scanIdle = new(true);
    private CancellationTokenSource? _scanCts;
    private int _closed;
    private bool _hashRun;
    private DirectoryCompareResult? _result;
    private string _leftPath = "";
    private string _rightPath = "";
    private bool _advanced;
    private bool _hashFiles;
    private bool _hashSourceForCompare;
    private bool _fatTimestampTolerance;
    private bool _filterFolders = true;
    private bool _filterFiles = true;
    private bool _filterSize;
    private bool _filterTimestamp;
    private bool _filterHash;
    private bool _filterName;
    private bool _filterPerFolder;
    private bool _isScanning;
    private bool _isPaused;
    private bool _updatingFilters;
    private int _copyDirectionIndex;
    private int _filesVisited;
    private int _foldersVisited;
    private bool _showDestOnly = true;
    private string _progressText = "";
    private string _stageText = "";
    private string _stageClockText = "";
    private string _stagePercentText = "—";
    private string _fileEtaText = "File  —  —";
    private string _folderEtaText = "Folder  —  —";
    private string _compareEtaText = "Compare  —";
    private bool _progressIndeterminate = true;
    private double _progressMaximum = 1;
    private double _progressValue;
    private string _statusText = "Pick Source and Destination folders, then Compare.";
    private string _summaryText = "";
    private string _listCaption = "Differences";
    private string _lastExportPath = "";

    public CompareViewModel(AppPaths paths, JobScheduler? scheduler = null)
    {
        _paths = paths;
        _scheduler = scheduler;
        _dispatcher = Dispatcher.CurrentDispatcher;
        if (_scheduler is not null)
        {
            _scheduler.CompareActivity += OnCompareActivity;
        }
        BrowseLeftCommand = new RelayCommand(BrowseLeft, () => !IsScanning);
        BrowseRightCommand = new RelayCommand(BrowseRight, () => !IsScanning);
        CompareCommand = new RelayCommand(StartScan, () => CanStartScan);
        PauseCommand = new RelayCommand(Pause, () => IsScanning && !IsPaused);
        ResumeCommand = new RelayCommand(Resume, () => IsScanning && IsPaused);
        CancelCommand = new RelayCommand(Cancel, () => IsScanning);
        ExportCommand = new RelayCommand(Export, () => HasCompletedResult);
        CreateJobCommand = new RelayCommand(CreateJob, () => HasCompletedResult);
        OfferSavedCompare();
        ReloadRecents();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Action<string, string>? QueueCatchUpRequested { get; set; }

    public ObservableCollection<string> LeftRecents { get; } = [];
    public ObservableCollection<string> RightRecents { get; } = [];
    public ObservableCollection<DirectoryCompareHighlight> Highlights { get; } = [];
    public ObservableCollection<DirectoryCompareDiff> ListedDifferences { get; } = [];

    public ICommand BrowseLeftCommand { get; }
    public ICommand BrowseRightCommand { get; }
    public ICommand CompareCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CreateJobCommand { get; }

    public string LeftPath
    {
        get => _leftPath;
        set
        {
            if (SetField(ref _leftPath, value ?? ""))
            {
                RaiseCommands();
            }
        }
    }

    public string RightPath
    {
        get => _rightPath;
        set
        {
            if (SetField(ref _rightPath, value ?? ""))
            {
                RaiseCommands();
            }
        }
    }

    public bool Advanced
    {
        get => _advanced;
        set
        {
            if (!SetField(ref _advanced, value))
            {
                return;
            }

            if (_updatingFilters)
            {
                return;
            }

            _updatingFilters = true;
            try
            {
                if (value)
                {
                    FilterSize = true;
                    FilterTimestamp = true;
                    FilterName = true;
                    FilterPerFolder = true;
                }
                else
                {
                    HashFiles = false;
                    FilterSize = false;
                    FilterTimestamp = false;
                    FilterHash = false;
                    FilterName = false;
                    FilterPerFolder = false;
                }
            }
            finally
            {
                _updatingFilters = false;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAdvancedOptions)));
            RefreshView();
        }
    }

    public bool ShowAdvancedOptions => Advanced;

    public bool HashFiles
    {
        get => _hashFiles;
        set
        {
            if (!SetField(ref _hashFiles, value))
            {
                return;
            }

            if (value)
            {
                FilterHash = true;
            }
            else if (!_updatingFilters)
            {
                FilterHash = false;
            }
        }
    }

    /// <summary>Compare tab: write the file list and source hashes. Destination hashes still run when Hash is on.</summary>
    public bool HashSourceForCompare
    {
        get => _hashSourceForCompare;
        set => SetField(ref _hashSourceForCompare, value);
    }

    public bool FatTimestampTolerance
    {
        get => _fatTimestampTolerance;
        set => SetField(ref _fatTimestampTolerance, value);
    }

    public bool FilterFolders
    {
        get => _filterFolders;
        set
        {
            if (SetField(ref _filterFolders, value))
            {
                RefreshView();
            }
        }
    }

    public bool FilterFiles
    {
        get => _filterFiles;
        set
        {
            if (SetField(ref _filterFiles, value))
            {
                RefreshView();
            }
        }
    }

    public bool FilterSize
    {
        get => _filterSize;
        set
        {
            if (SetField(ref _filterSize, value))
            {
                RefreshView();
            }
        }
    }

    public bool FilterTimestamp
    {
        get => _filterTimestamp;
        set
        {
            if (SetField(ref _filterTimestamp, value))
            {
                RefreshView();
            }
        }
    }

    public bool FilterHash
    {
        get => _filterHash;
        set
        {
            if (SetField(ref _filterHash, value))
            {
                RefreshView();
            }
        }
    }

    public bool FilterName
    {
        get => _filterName;
        set
        {
            if (SetField(ref _filterName, value))
            {
                RefreshView();
            }
        }
    }

    public bool FilterPerFolder
    {
        get => _filterPerFolder;
        set
        {
            if (SetField(ref _filterPerFolder, value))
            {
                RefreshView();
            }
        }
    }

    public int CopyDirectionIndex
    {
        get => _copyDirectionIndex;
        set => SetField(ref _copyDirectionIndex, value);
    }

    public bool ShowDestOnly
    {
        get => _showDestOnly;
        set
        {
            if (SetField(ref _showDestOnly, value))
            {
                RefreshView();
            }
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PathsEditable)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStartScan)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowProgress)));
                RaiseCommands();
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetField(ref _isPaused, value))
            {
                RaiseCommands();
            }
        }
    }

    public bool PathsEditable => !IsScanning;
    public bool ShowProgress => IsScanning || FilesVisited > 0;
    public bool HasCompletedResult => _result is { Completed: true };
    public bool HasHighlights => Highlights.Count > 0;
    public bool HasListedDifferences => ListedDifferences.Count > 0;
    public bool HasSummary => !string.IsNullOrWhiteSpace(SummaryText);

    public int FilesVisited
    {
        get => _filesVisited;
        private set
        {
            if (SetField(ref _filesVisited, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowProgress)));
            }
        }
    }

    public int FoldersVisited
    {
        get => _foldersVisited;
        private set => SetField(ref _foldersVisited, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    public string StageText
    {
        get => _stageText;
        private set => SetField(ref _stageText, value);
    }

    public string StageClockText
    {
        get => _stageClockText;
        private set => SetField(ref _stageClockText, value);
    }

    public string StagePercentText
    {
        get => _stagePercentText;
        private set => SetField(ref _stagePercentText, value);
    }

    public string FileEtaText
    {
        get => _fileEtaText;
        private set => SetField(ref _fileEtaText, value);
    }

    public string FolderEtaText
    {
        get => _folderEtaText;
        private set => SetField(ref _folderEtaText, value);
    }

    public string CompareEtaText
    {
        get => _compareEtaText;
        private set => SetField(ref _compareEtaText, value);
    }

    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        private set => SetField(ref _progressIndeterminate, value);
    }

    public double ProgressMaximum
    {
        get => _progressMaximum;
        private set => SetField(ref _progressMaximum, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetField(ref _progressValue, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set
        {
            if (SetField(ref _summaryText, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSummary)));
            }
        }
    }

    public string ListCaption
    {
        get => _listCaption;
        private set => SetField(ref _listCaption, value);
    }

    public string LastExportPath
    {
        get => _lastExportPath;
        private set => SetField(ref _lastExportPath, value);
    }

    public DirectoryCompareResult? Result => _result;

    public CompareFilter CurrentFilter
    {
        get
        {
            var filter = CompareFilter.None;
            if (FilterFolders)
            {
                filter |= CompareFilter.FolderCounts;
            }

            if (FilterFiles)
            {
                filter |= CompareFilter.FileCounts;
            }

            if (FilterSize)
            {
                filter |= CompareFilter.Size;
            }

            if (FilterTimestamp)
            {
                filter |= CompareFilter.Timestamp;
            }

            if (FilterHash)
            {
                filter |= CompareFilter.Hash;
            }

            if (FilterName)
            {
                filter |= CompareFilter.Name;
            }

            if (FilterPerFolder)
            {
                filter |= CompareFilter.PerFolderFileCounts;
            }

            return filter;
        }
    }

    public bool CanStartScan =>
        !IsScanning && !string.IsNullOrWhiteSpace(LeftPath) && !string.IsNullOrWhiteSpace(RightPath);

    public void LoadResultForTests(DirectoryCompareResult result)
    {
        LeftPath = result.LeftRoot;
        RightPath = result.RightRoot;
        ApplyResult(result);
    }

    public string ExportTextForTests() =>
        _result is null ? "" : DirectoryCompareReport.Build(_result, CurrentFilter);

    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1);
        if (_scheduler is not null)
        {
            _scheduler.CompareActivity -= OnCompareActivity;
        }

        _scanCts?.Cancel();
        _pause.Resume();
        _scanIdle.Wait(TimeSpan.FromSeconds(5));
        _scanCts?.Dispose();
        _scanIdle.Dispose();
    }

    private void StartScan()
    {
        if (!CanStartScan)
        {
            return;
        }

        if (_scheduler is not null)
        {
            StartQueuedCompare();
            return;
        }

        _scanCts?.Cancel();
        _pause.Resume();
        _scanIdle.Wait(TimeSpan.FromSeconds(5));
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        _scanIdle.Reset();
        _pause.Resume();
        IsPaused = false;
        IsScanning = true;
        FilesVisited = 0;
        FoldersVisited = 0;
        ProgressText = "Starting…";
        StageText = "";
        StageClockText = "";
        StagePercentText = "—";
        FileEtaText = "File  —  —";
        FolderEtaText = "Folder  —  —";
        CompareEtaText = "Compare  —  (counting)";
        ProgressIndeterminate = true;
        ProgressMaximum = 1;
        ProgressValue = 0;
        _hashRun = Advanced && HashFiles;
        var resuming = false;
        if (_hashRun)
        {
            var saved = CompareManifestStore.Load(_paths.CompareManifestFile);
            resuming = saved is { InventoryComplete: true } &&
                       CompareManifestStore.SameJob(saved, LeftPath, RightPath, Advanced, true, FatTimestampTolerance);
        }

        StatusText = resuming ? "Continuing saved compare…" : "Scanning…";
        Highlights.Clear();
        ListedDifferences.Clear();
        SummaryText = "";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasHighlights)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasListedDifferences)));
        Remember(isLeft: true, LeftPath);
        Remember(isLeft: false, RightPath);

        var left = LeftPath;
        var right = RightPath;
        var options = new DirectoryCompareOptions
        {
            Advanced = Advanced,
            Hash = HashFiles,
            FatTimestampTolerance = FatTimestampTolerance
        };
        var token = _scanCts.Token;
        var manifest = _hashRun ? _paths.CompareManifestFile : null;
        _ = Task.Run(() => RunScan(left, right, options, token, manifest), token);
    }

    private void RunScan(
        string left,
        string right,
        DirectoryCompareOptions options,
        CancellationToken token,
        string? manifest)
    {
        try
        {
            var result = DirectoryComparer.Compare(
                left,
                right,
                options,
                token,
                _pause,
                progress => RunOnUi(() => ApplyProgress(progress)),
                manifest);
            if (Volatile.Read(ref _closed) != 0)
            {
                return;
            }

            if (result.Completed && result.Hashed && manifest is not null)
            {
                CompareManifestStore.Delete(manifest);
            }

            RunOnUi(() => ApplyResult(result));
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref _closed) == 0)
            {
                RunOnUi(ApplyCanceled);
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _closed) != 0)
            {
                return;
            }

            RunOnUi(() =>
            {
                IsScanning = false;
                IsPaused = false;
                StatusText = ex.Message;
            });
        }
        finally
        {
            try
            {
                _scanIdle.Set();
            }
            catch (ObjectDisposedException)
            {
                // window already closed
            }
        }
    }

    private void ApplyProgress(DirectoryCompareProgress progress)
    {
        FilesVisited = progress.FilesVisited;
        FoldersVisited = progress.FoldersVisited;
        var pace = progress.Pace;
        var file = ComparePace.FileLabel(progress.CurrentRelative);
        var folder = pace?.CurrentFolder ?? ComparePace.FolderLabel(progress.CurrentRelative);
        FileEtaText = $"File  {file}  {ByteFormatter.Eta(pace?.FileEta)}";
        FolderEtaText = $"Folder  {folder}  {ByteFormatter.Eta(pace?.FolderEta)}";
        if (progress.Stage is { } stage)
        {
            StageText = stage.Line;
            StageClockText = ComparePipeline.ClockLine(stage.OverallElapsed, stage.Elapsed, stage.Remaining);
            if (stage.Percent is { } pct)
            {
                ProgressIndeterminate = false;
                ProgressMaximum = 100;
                ProgressValue = pct;
                StagePercentText = ProgressHeader.PercentLabel(pct, true);
            }
            else
            {
                ProgressIndeterminate = true;
                ProgressMaximum = 1;
                ProgressValue = 0;
                StagePercentText = "—";
            }

            CompareEtaText = stage.Kind is CompareStageKind.CountSource or CompareStageKind.CountDestination
                ? $"Compare  —  ({stage.Name.ToLowerInvariant()})"
                : $"Compare  {ByteFormatter.Eta(pace?.OverallEta)}";
        }
        else if (pace is { BytesTotal: > 0 })
        {
            ProgressIndeterminate = false;
            ProgressMaximum = pace.BytesTotal;
            ProgressValue = Math.Min(pace.BytesDone, pace.BytesTotal);
            CompareEtaText =
                $"Compare  {ByteFormatter.Eta(pace.OverallEta)}  ({ByteFormatter.ToString(pace.BytesDone)} / {ByteFormatter.ToString(pace.BytesTotal)})";
            StageText = "";
            StageClockText = "";
            StagePercentText = "—";
        }
        else
        {
            ProgressIndeterminate = true;
            ProgressMaximum = 1;
            ProgressValue = 0;
            CompareEtaText = "Compare  —  (counting)";
            StageText = "";
            StageClockText = "";
            StagePercentText = "—";
        }

        ProgressText = $"Visited {progress.FilesVisited:N0} files, {progress.FoldersVisited:N0} folders";
    }

    private void ApplyCanceled()
    {
        IsScanning = false;
        IsPaused = false;
        StatusText = CanceledStatus();
        ProgressText = $"Canceled after {FilesVisited:N0} files.";
        _result = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCompletedResult)));
        RaiseCommands();
    }

    private void ApplyResult(DirectoryCompareResult result)
    {
        _result = result;
        IsScanning = false;
        IsPaused = false;
        FilesVisited = result.FilesVisited;
        FoldersVisited = result.FoldersVisited;
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            StatusText = result.Error;
        }
        else if (result.Canceled)
        {
            StatusText = CanceledStatus();
        }
        else
        {
            StatusText = result.Advanced
                ? (result.Hashed ? "Advanced scan complete (hashed)." : "Advanced scan complete.")
                : "Default count complete.";
        }

        ProgressText = result.Canceled
            ? $"Canceled after {result.FilesVisited:N0} files."
            : $"Visited {result.FilesVisited:N0} files, {result.FoldersVisited:N0} folders.";
        RefreshView();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCompletedResult)));
        RaiseCommands();
    }

    private void RefreshView()
    {
        if (_result is null)
        {
            return;
        }

        var filter = CurrentFilter;
        SummaryText = BuildSummary(_result, filter);
        Highlights.Clear();
        foreach (var card in _result.Highlights(filter, 5, ShowDestOnly))
        {
            Highlights.Add(card);
        }

        ListedDifferences.Clear();
        const int cap = 2_000;
        var listed = _result.Listed(filter, cap, ShowDestOnly);
        foreach (var row in listed)
        {
            ListedDifferences.Add(row);
        }

        var total = _result.Filtered(filter, ShowDestOnly).Count();
        var hiddenDestOnly = ShowDestOnly
            ? 0
            : _result.Filtered(filter).Count(d => d.IsDestOnly);
        if (total > listed.Count)
        {
            ListCaption = $"Differences ({total:N0}; showing {listed.Count:N0} — export TXT for the full list)";
        }
        else if (hiddenDestOnly > 0)
        {
            ListCaption = $"Differences ({total:N0}; {hiddenDestOnly:N0} destination-only hidden)";
        }
        else
        {
            ListCaption = $"Differences ({total:N0})";
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasHighlights)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasListedDifferences)));
    }

    private static string BuildSummary(DirectoryCompareResult result, CompareFilter filter)
    {
        var lines = new List<string>
        {
            $"Folders  Source {result.LeftFolders}  Destination {result.RightFolders}",
            $"Files  Source {result.LeftFiles}  Destination {result.RightFiles}"
        };
        if (filter.HasFlag(CompareFilter.FolderCounts))
        {
            lines.Add($"Folder diffs  source only {result.FoldersOnlyLeft}  dest only {result.FoldersOnlyRight}  matching name {result.FoldersMatchingName}");
        }

        if (filter.HasFlag(CompareFilter.FileCounts))
        {
            lines.Add($"File diffs  source only {result.FilesOnlyLeft}  dest only {result.FilesOnlyRight}  same relative path {result.FilesSameRelativePath}");
        }

        if (result.Advanced)
        {
            if (filter.HasFlag(CompareFilter.Size))
            {
                lines.Add($"Size mismatches  {result.SizeMismatches}");
            }

            if (filter.HasFlag(CompareFilter.Timestamp))
            {
                lines.Add($"Timestamp mismatches  {result.TimestampMismatches}");
            }

            if (filter.HasFlag(CompareFilter.Hash))
            {
                lines.Add($"Hash mismatches  {result.HashMismatches}");
            }

            if (filter.HasFlag(CompareFilter.Name))
            {
                lines.Add($"Name issues  {result.NameMismatches}");
            }

            if (filter.HasFlag(CompareFilter.PerFolderFileCounts))
            {
                lines.Add($"Per-folder file-count mismatches  {result.FolderFileCountMismatches}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void Pause()
    {
        if (_scheduler is not null && _activeJobId is not null && _scheduler.TryGetRunningJob(_activeJobId) is not null)
        {
            _scheduler.Pause(_activeJobId);
            IsPaused = true;
            StatusText = "Compare paused.";
            return;
        }

        _pause.Pause();
        IsPaused = true;
        StatusText = "Scan paused.";
    }

    private void Resume()
    {
        if (_scheduler is not null && _activeJobId is not null)
        {
            if (_scheduler.TryGetRunningJob(_activeJobId) is not null)
            {
                _scheduler.ResumePaused(_activeJobId);
            }
            else
            {
                _scheduler.ResumeOrRetry(_activeJobId);
            }

            IsPaused = false;
            IsScanning = true;
            StatusText = "Comparing…";
            return;
        }

        _pause.Resume();
        IsPaused = false;
        StatusText = "Scanning…";
    }

    private void Cancel()
    {
        if (_scheduler is not null && _activeJobId is not null)
        {
            if (_scheduler.TryGetRunningJob(_activeJobId) is not null)
            {
                _scheduler.Stop(_activeJobId);
            }
            else
            {
                var queued = _scheduler.Queue.FirstOrDefault(job => job.Id == _activeJobId);
                if (queued is { Status: JobStatus.Pending })
                {
                    _scheduler.Remove(_activeJobId);
                    IsScanning = false;
                    IsPaused = false;
                    StatusText = "Compare removed from the queue.";
                    return;
                }

                _scheduler.Stop(_activeJobId);
            }

            StatusText = "Canceling…";
            return;
        }

        _scanCts?.Cancel();
        _pause.Resume();
        StatusText = "Canceling…";
    }

    private void Export()
    {
        if (_result is not { Completed: true })
        {
            return;
        }

        Directory.CreateDirectory(_paths.Compares);
        var dlg = new SaveFileDialog
        {
            Title = "Export compare report",
            Filter = "Text (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = DirectoryCompareReport.DefaultFileName(),
            InitialDirectory = _paths.Compares
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            DirectoryCompareReport.Write(dlg.FileName, _result, CurrentFilter);
            LastExportPath = dlg.FileName;
            StatusText = "Exported " + dlg.FileName;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private void CreateJob()
    {
        if (!HasCompletedResult)
        {
            return;
        }

        var leftToRight = CopyDirectionIndex == 0;
        var source = leftToRight ? LeftPath : RightPath;
        var dest = leftToRight ? RightPath : LeftPath;
        QueueCatchUpRequested?.Invoke(source, dest);
        StatusText = leftToRight
            ? "Queued catch-up Source → Dest. Enumeration skips files that already match dest."
            : "Queued catch-up Dest → Source. Enumeration skips files that already match dest.";
    }

    private void BrowseLeft() => Browse(isLeft: true);

    private void BrowseRight() => Browse(isLeft: false);

    private void Browse(bool isLeft)
    {
        var recents = LibraryStore.LoadRecents(_paths);
        var current = isLeft ? LeftPath : RightPath;
        var last = isLeft ? recents.LastCompareLeftDir : recents.LastCompareRightDir;
        var start = LibraryStore.BrowseStartDir(last, current);
        var folder = new OpenFolderDialog { Title = isLeft ? "Compare — Source folder" : "Compare — Destination folder" };
        if (!string.IsNullOrEmpty(start))
        {
            folder.InitialDirectory = start;
        }

        if (folder.ShowDialog() != true || string.IsNullOrWhiteSpace(folder.FolderName))
        {
            return;
        }

        if (isLeft)
        {
            LeftPath = folder.FolderName;
        }
        else
        {
            RightPath = folder.FolderName;
        }

        Remember(isLeft, folder.FolderName);
    }

    private void Remember(bool isLeft, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var recents = LibraryStore.LoadRecents(_paths);
        if (isLeft)
        {
            LibraryStore.Remember(recents.CompareLeft, path);
            var dir = LibraryStore.ExistingDirectoryOf(path);
            if (!string.IsNullOrEmpty(dir))
            {
                recents.LastCompareLeftDir = dir;
            }

            Replace(LeftRecents, recents.CompareLeft);
        }
        else
        {
            LibraryStore.Remember(recents.CompareRight, path);
            var dir = LibraryStore.ExistingDirectoryOf(path);
            if (!string.IsNullOrEmpty(dir))
            {
                recents.LastCompareRightDir = dir;
            }

            Replace(RightRecents, recents.CompareRight);
        }

        LibraryStore.SaveRecents(_paths, recents);
    }

    private void ReloadRecents()
    {
        var recents = LibraryStore.LoadRecents(_paths);
        Replace(LeftRecents, recents.CompareLeft);
        Replace(RightRecents, recents.CompareRight);
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private void RaiseCommands()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStartScan)));
        (BrowseLeftCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseRightCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CompareCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PauseCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResumeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CreateJobCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action);
    }

    private void StartQueuedCompare()
    {
        var existing = FindOpenCompare();
        Job job;
        if (existing is not null)
        {
            job = existing;
            _activeJobId = job.Id;
            _hashRun = job.Options.CompareHash || job.Options.HashSourceForCompare;
            PrepareScanUi(resuming: true);
            StatusText = "Comparing…";
            _scheduler!.ResumeOrRetry(job.Id);
            return;
        }

        var leaf = Path.GetFileName(LeftPath.TrimEnd('\\', '/'));
        job = new Job
        {
            Kind = JobKind.Compare,
            Name = string.IsNullOrWhiteSpace(leaf) ? "Compare" : "Compare " + leaf,
            SourcePath = LeftPath,
            SourcePaths = [LeftPath],
            DestinationPath = RightPath,
            SourceKind = SourceKind.Folder,
            Options = new JobOptions
            {
                CompareAdvanced = Advanced,
                CompareHash = Advanced && HashFiles,
                HashSourceForCompare = HashSourceForCompare,
                FatTimestampTolerance = FatTimestampTolerance,
                IncludeSourceFolderName = false
            }
        };
        _activeJobId = job.Id;
        _hashRun = job.Options.CompareHash || job.Options.HashSourceForCompare;
        PrepareScanUi(resuming: false);
        StatusText = "Comparing…";
        Remember(isLeft: true, LeftPath);
        Remember(isLeft: false, RightPath);
        _scheduler!.Enqueue(job, startNow: true);
    }

    private Job? FindOpenCompare()
    {
        if (_scheduler is null)
        {
            return null;
        }

        foreach (var job in _scheduler.Queue)
        {
            if (job.Kind != JobKind.Compare || job.Status == JobStatus.Completed)
            {
                continue;
            }

            var marker = new CompareManifest
            {
                LeftRoot = job.SourcePath,
                RightRoot = job.DestinationPath,
                Advanced = job.Options.CompareAdvanced,
                Hash = job.Options.CompareHash,
                FatTimestampTolerance = job.Options.FatTimestampTolerance
            };
            if (job.Options.HashSourceForCompare != HashSourceForCompare)
            {
                continue;
            }

            if (CompareManifestStore.SameJob(marker, LeftPath, RightPath, Advanced, Advanced && HashFiles, FatTimestampTolerance))
            {
                return job;
            }
        }

        return null;
    }

    private void PrepareScanUi(bool resuming)
    {
        _pause.Resume();
        IsPaused = false;
        IsScanning = true;
        FilesVisited = 0;
        FoldersVisited = 0;
        ProgressText = resuming ? "Continuing…" : "Starting…";
        StageText = "";
        StageClockText = "";
        StagePercentText = "—";
        FileEtaText = "File  —  —";
        FolderEtaText = "Folder  —  —";
        CompareEtaText = "Compare  —  (counting)";
        ProgressIndeterminate = true;
        ProgressMaximum = 1;
        ProgressValue = 0;
        Highlights.Clear();
        ListedDifferences.Clear();
        SummaryText = "";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasHighlights)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasListedDifferences)));
    }

    private void OnCompareActivity(object? sender, CompareActivity activity)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return;
        }

        if (!string.IsNullOrEmpty(_activeJobId)
            && !string.Equals(_activeJobId, activity.JobId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RunOnUi(() =>
        {
            if (activity.Job is not null)
            {
                IsPaused = activity.Job.Status is JobStatus.Paused or JobStatus.PausedOutsideHours;
            }

            if (activity.Progress is { } progress)
            {
                IsScanning = activity.Result is null
                    && activity.Job?.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled);
                ApplyProgress(progress);
            }

            if (activity.Result is { } result)
            {
                if (result.Completed || !string.IsNullOrWhiteSpace(result.Error))
                {
                    ApplyResult(result);
                }
                else
                {
                    ApplyCanceled();
                }
            }
        });
    }

    private void OfferSavedCompare()
    {
        if (_scheduler is not null)
        {
            var pending = _scheduler.Queue.FirstOrDefault(job =>
                job.Kind == JobKind.Compare && job.Status != JobStatus.Completed);
            if (pending is not null)
            {
                Advanced = pending.Options.CompareAdvanced;
                HashFiles = pending.Options.CompareHash;
                HashSourceForCompare = pending.Options.HashSourceForCompare;
                FatTimestampTolerance = pending.Options.FatTimestampTolerance;
                LeftPath = pending.SourcePath;
                RightPath = pending.DestinationPath;
                _activeJobId = pending.Id;
                StatusText = "This compare is in the queue. Compare or Resume continues it.";
                return;
            }
        }

        var saved = CompareManifestStore.Load(_paths.CompareManifestFile);
        if (saved is not { InventoryComplete: true })
        {
            return;
        }

        Advanced = saved.Advanced;
        HashFiles = saved.Hash;
        FatTimestampTolerance = saved.FatTimestampTolerance;
        LeftPath = saved.LeftRoot;
        RightPath = saved.RightRoot;
        StatusText = SavedCompareStatus(saved);
    }

    private string CanceledStatus()
    {
        if (_scheduler is not null)
        {
            return "Compare saved in the queue. Resume continues it.";
        }

        if (!_hashRun)
        {
            return "Scan canceled.";
        }

        var saved = CompareManifestStore.Load(_paths.CompareManifestFile);
        return saved is { InventoryComplete: true }
            ? "Compare saved. Click Compare to continue."
            : "Scan canceled.";
    }

    private static string SavedCompareStatus(CompareManifest saved)
    {
        if (saved.ComparedFileCount > 0)
        {
            return $"Saved compare. {saved.ComparedFileCount:N0} files already compared. Compare continues.";
        }

        if (saved.SourceHashCount > 0)
        {
            return $"Saved compare. {saved.SourceHashCount:N0} source hashes saved. Compare continues with the destination.";
        }

        return "Saved compare. File list is saved. Compare continues with hashing.";
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
