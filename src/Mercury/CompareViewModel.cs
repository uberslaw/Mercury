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
    private readonly Dispatcher _dispatcher;
    private readonly PauseGate _pause = new();
    private CancellationTokenSource? _scanCts;
    private DirectoryCompareResult? _result;
    private string _leftPath = "";
    private string _rightPath = "";
    private bool _advanced;
    private bool _hashFiles;
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
    private string _progressText = "";
    private string _statusText = "Pick Left and Right folders, then Compare.";
    private string _summaryText = "";
    private string _listCaption = "Differences";
    private string _lastExportPath = "";

    public CompareViewModel(AppPaths paths)
    {
        _paths = paths;
        _dispatcher = Dispatcher.CurrentDispatcher;
        BrowseLeftCommand = new RelayCommand(BrowseLeft, () => !IsScanning);
        BrowseRightCommand = new RelayCommand(BrowseRight, () => !IsScanning);
        CompareCommand = new RelayCommand(StartScan, () => CanStartScan);
        PauseCommand = new RelayCommand(Pause, () => IsScanning && !IsPaused);
        ResumeCommand = new RelayCommand(Resume, () => IsScanning && IsPaused);
        CancelCommand = new RelayCommand(Cancel, () => IsScanning);
        ExportCommand = new RelayCommand(Export, () => HasCompletedResult);
        CreateJobCommand = new RelayCommand(CreateJob, () => HasCompletedResult);
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
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _pause.Resume();
    }

    private void StartScan()
    {
        if (!CanStartScan)
        {
            return;
        }

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        _pause.Resume();
        IsPaused = false;
        IsScanning = true;
        FilesVisited = 0;
        FoldersVisited = 0;
        ProgressText = "Starting…";
        StatusText = "Scanning…";
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
        _ = Task.Run(() => RunScan(left, right, options, token), token);
    }

    private void RunScan(string left, string right, DirectoryCompareOptions options, CancellationToken token)
    {
        try
        {
            var result = DirectoryComparer.Compare(
                left,
                right,
                options,
                token,
                _pause,
                progress => RunOnUi(() =>
                {
                    FilesVisited = progress.FilesVisited;
                    FoldersVisited = progress.FoldersVisited;
                    var current = string.IsNullOrEmpty(progress.CurrentRelative) ? "" : "  " + progress.CurrentRelative;
                    ProgressText = $"Visited {progress.FilesVisited:N0} files, {progress.FoldersVisited:N0} folders{current}";
                }));
            RunOnUi(() => ApplyResult(result));
        }
        catch (OperationCanceledException)
        {
            RunOnUi(() => ApplyCanceled());
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                IsScanning = false;
                IsPaused = false;
                StatusText = ex.Message;
            });
        }
    }

    private void ApplyCanceled()
    {
        IsScanning = false;
        IsPaused = false;
        StatusText = "Scan canceled.";
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
            StatusText = "Scan canceled.";
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
        foreach (var card in _result.Highlights(filter, 5))
        {
            Highlights.Add(card);
        }

        ListedDifferences.Clear();
        const int cap = 2_000;
        var listed = _result.Listed(filter, cap);
        foreach (var row in listed)
        {
            ListedDifferences.Add(row);
        }

        var total = _result.Filtered(filter).Count();
        ListCaption = total > listed.Count
            ? $"Differences ({total:N0}; showing {listed.Count:N0} — export TXT for the full list)"
            : $"Differences ({total:N0})";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasHighlights)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasListedDifferences)));
    }

    private static string BuildSummary(DirectoryCompareResult result, CompareFilter filter)
    {
        var lines = new List<string>
        {
            $"Folders  Left {result.LeftFolders}  Right {result.RightFolders}",
            $"Files  Left {result.LeftFiles}  Right {result.RightFiles}"
        };
        if (filter.HasFlag(CompareFilter.FolderCounts))
        {
            lines.Add($"Folder diffs  only left {result.FoldersOnlyLeft}  only right {result.FoldersOnlyRight}  matching name {result.FoldersMatchingName}");
        }

        if (filter.HasFlag(CompareFilter.FileCounts))
        {
            lines.Add($"File diffs  only left {result.FilesOnlyLeft}  only right {result.FilesOnlyRight}  same relative path {result.FilesSameRelativePath}");
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
        _pause.Pause();
        IsPaused = true;
        StatusText = "Scan paused.";
    }

    private void Resume()
    {
        _pause.Resume();
        IsPaused = false;
        StatusText = "Scanning…";
    }

    private void Cancel()
    {
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
            ? "Queued catch-up Left → Right. Enumeration skips files that already match dest."
            : "Queued catch-up Right → Left. Enumeration skips files that already match dest.";
    }

    private void BrowseLeft() => Browse(isLeft: true);

    private void BrowseRight() => Browse(isLeft: false);

    private void Browse(bool isLeft)
    {
        var recents = LibraryStore.LoadRecents(_paths);
        var current = isLeft ? LeftPath : RightPath;
        var last = isLeft ? recents.LastCompareLeftDir : recents.LastCompareRightDir;
        var start = LibraryStore.BrowseStartDir(last, current);
        var folder = new OpenFolderDialog { Title = isLeft ? "Compare — Left folder" : "Compare — Right folder" };
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

        _dispatcher.Invoke(action);
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
