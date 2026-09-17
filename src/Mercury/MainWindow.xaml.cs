using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Mercury;

public partial class MainWindow : Window
{
    private ScrollViewer? _consoleScroll;
    private ThemePreviewWindow? _preview;
    private ThemeEditorWindow? _editor;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        vm.AttachConsoleView(CollectionViewSource.GetDefaultView(vm.ConsoleLines));
        ConsoleList.ItemsSource = vm.ConsoleView;
        vm.ConsoleLines.CollectionChanged += OnConsoleLinesChanged;
        vm.PropertyChanged += OnVmPropertyChanged;
        ConsoleList.Loaded += ConsoleList_Loaded;
        vm.Theme.OpenPreviewRequested = OpenThemePreview;
        vm.Theme.PopOutEditorRequested = OpenThemeEditor;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void HelpMenu_Click(object sender, RoutedEventArgs e)
    {
        if (HelpTab is not null)
        {
            HelpTab.IsSelected = true;
        }
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _closing || !ReferenceEquals(e.OriginalSource, MainTabs))
        {
            return;
        }

        if (ThemeTab is { IsSelected: true })
        {
            ThemeChrome.SetHelpersEnabled(true);
            OpenThemePreview();
            return;
        }

        LeaveThemeSession();
    }

    private void LeaveThemeSession()
    {
        ThemeChrome.SetHelpersEnabled(false);
        Vm.Theme.ClearRowHighlights();
        CloseThemePreview();
    }

    private void CloseThemePreview()
    {
        if (_preview is not { } preview)
        {
            return;
        }

        try
        {
            preview.Close();
        }
        catch
        {
            _preview = null;
        }
    }

    private void OpenThemePreview()
    {
        if (ThemeTab is not { IsSelected: true })
        {
            return;
        }

        ThemeChrome.SetHelpersEnabled(true);
        if (_preview is { IsLoaded: true })
        {
            _preview.Activate();
            return;
        }

        _preview = new ThemePreviewWindow { Owner = this };
        _preview.Closed += OnPreviewClosed;
        _preview.Deactivated += OnPreviewDeactivated;
        PlaceBeside(this, _preview, 8);
        _preview.Show();
    }

    private void OnPreviewClosed(object? sender, EventArgs e)
    {
        if (sender is ThemePreviewWindow window)
        {
            window.Closed -= OnPreviewClosed;
            window.Deactivated -= OnPreviewDeactivated;
        }

        _preview = null;
        ThemeChrome.Unpin();
        if (ThemeTab is not { IsSelected: true })
        {
            ThemeChrome.SetHelpersEnabled(false);
            Vm.Theme.ClearRowHighlights();
        }
    }

    private void OnPreviewDeactivated(object? sender, EventArgs e)
    {
        // Keep a pin while moving to the Theme editor or the colour picker.
        if (_closing)
        {
            return;
        }

        ThemeChrome.ClearHover();
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        ThemeChrome.ClearHover();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape || ThemeTab is not { IsSelected: true })
        {
            return;
        }

        if (ThemeChrome.Unpin())
        {
            e.Handled = true;
        }
    }

    private void OpenThemeEditor()
    {
        if (_editor is { IsLoaded: true })
        {
            _editor.Activate();
            return;
        }

        _editor = new ThemeEditorWindow(Vm.Theme) { Owner = this };
        _preview?.Activate();
        _editor.Closed += (_, _) => _editor = null;
        PlaceBeside(this, _editor, 16);
        _editor.Show();
    }

    private static void PlaceBeside(Window owner, Window child, double gap)
    {
        child.WindowStartupLocation = WindowStartupLocation.Manual;
        child.Left = owner.Left + owner.ActualWidth + gap;
        child.Top = owner.Top;
        var work = SystemParameters.WorkArea;
        if (child.Left + child.Width > work.Right)
        {
            child.Left = Math.Max(work.Left, owner.Left - child.Width - gap);
        }

        if (child.Top + child.Height > work.Bottom)
        {
            child.Top = Math.Max(work.Top, work.Bottom - child.Height);
        }
    }

    private void ConsoleList_Loaded(object sender, RoutedEventArgs e)
    {
        _consoleScroll = FindScrollViewer(ConsoleList);
        if (_consoleScroll is not null)
        {
            _consoleScroll.ScrollChanged += ConsoleScroll_ScrollChanged;
        }

        if (Vm.FollowConsole)
        {
            ScrollConsoleToLatest();
        }
    }

    private void ConsoleScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var atBottom = IsConsoleAtBottom();
        if (string.IsNullOrEmpty(Vm.ConsoleSearch))
        {
            Vm.FollowConsole = atBottom;
            return;
        }

        if (!atBottom)
        {
            Vm.FollowConsole = false;
        }
    }

    private void OnConsoleLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnConsoleLinesChanged(sender, e));
            return;
        }

        if (!Vm.FollowConsole)
        {
            return;
        }

        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            ScrollConsoleToLatest();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.FollowConsole) && Vm.FollowConsole)
        {
            ScrollConsoleToLatest();
        }
    }

    private void ScrollConsoleToLatest()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ConsoleList.Items.Count == 0)
            {
                return;
            }

            ConsoleList.ScrollIntoView(ConsoleList.Items[ConsoleList.Items.Count - 1]);
        }, DispatcherPriority.Loaded);
    }

    private bool IsConsoleAtBottom()
    {
        if (_consoleScroll is null)
        {
            return true;
        }

        const double epsilon = 0.5;
        return _consoleScroll.ScrollableHeight <= 0
               || _consoleScroll.VerticalOffset >= _consoleScroll.ScrollableHeight - epsilon;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindScrollViewer(child);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private void CatcherPassphrase_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
        {
            Vm.SetCatcherPassphrase(box.Password);
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        if (_consoleScroll is not null)
        {
            _consoleScroll.ScrollChanged -= ConsoleScroll_ScrollChanged;
        }

        Vm.ConsoleLines.CollectionChanged -= OnConsoleLinesChanged;
        Vm.PropertyChanged -= OnVmPropertyChanged;
        Vm.Theme.OpenPreviewRequested = null;
        Vm.Theme.PopOutEditorRequested = null;
        ThemeChrome.SetHelpersEnabled(false);
        Vm.Theme.ClearRowHighlights();
        _preview?.Close();
        _editor?.Close();
        Vm.Closing();
        Vm.Dispose();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Path_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedPath(e, out var path))
        {
            Vm.SetDroppedSource(path);
        }
    }

    private void Source_Drop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedPath(e, out var path))
        {
            Vm.SetDroppedSource(path);
            e.Handled = true;
        }
    }

    private void Dest_Drop(object sender, DragEventArgs e)
    {
        if (TryGetDroppedPath(e, out var path))
        {
            Vm.SetDroppedDest(path);
            e.Handled = true;
        }
    }

    private static bool TryGetDroppedPath(DragEventArgs e, out string path)
    {
        path = "";
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return false;
        }

        path = paths[0];
        return true;
    }
}

