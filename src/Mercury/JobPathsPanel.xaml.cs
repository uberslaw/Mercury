using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mercury;

public partial class JobPathsPanel : UserControl
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(JobPathsPanel),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty DestPathProperty = DependencyProperty.Register(
        nameof(DestPath), typeof(string), typeof(JobPathsPanel),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty DestKindIndexProperty = DependencyProperty.Register(
        nameof(DestKindIndex), typeof(int), typeof(JobPathsPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty DestIsFolderProperty = DependencyProperty.Register(
        nameof(DestIsFolder), typeof(bool), typeof(JobPathsPanel),
        new PropertyMetadata(true));

    public static readonly DependencyProperty DestIsCatcherProperty = DependencyProperty.Register(
        nameof(DestIsCatcher), typeof(bool), typeof(JobPathsPanel),
        new PropertyMetadata(false));

    public static readonly DependencyProperty PathsEditableProperty = DependencyProperty.Register(
        nameof(PathsEditable), typeof(bool), typeof(JobPathsPanel),
        new PropertyMetadata(true));

    public static readonly DependencyProperty HasSourceFoldersProperty = DependencyProperty.Register(
        nameof(HasSourceFolders), typeof(bool), typeof(JobPathsPanel),
        new PropertyMetadata(false));

    public static readonly DependencyProperty IncludeSourceFolderNameProperty = DependencyProperty.Register(
        nameof(IncludeSourceFolderName), typeof(bool), typeof(JobPathsPanel),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty LandingPreviewProperty = DependencyProperty.Register(
        nameof(LandingPreview), typeof(string), typeof(JobPathsPanel),
        new PropertyMetadata(""));

    public static readonly DependencyProperty ShowLandingPreviewProperty = DependencyProperty.Register(
        nameof(ShowLandingPreview), typeof(bool), typeof(JobPathsPanel),
        new PropertyMetadata(false));

    public static readonly DependencyProperty ShowCatcherStatusProperty = DependencyProperty.Register(
        nameof(ShowCatcherStatus), typeof(bool), typeof(JobPathsPanel),
        new PropertyMetadata(false));

    public static readonly DependencyProperty CatcherOnlineStatusProperty = DependencyProperty.Register(
        nameof(CatcherOnlineStatus), typeof(string), typeof(JobPathsPanel),
        new PropertyMetadata(""));

    public static readonly DependencyProperty CatcherTransferHintProperty = DependencyProperty.Register(
        nameof(CatcherTransferHint), typeof(string), typeof(JobPathsPanel),
        new PropertyMetadata(""));

    public static readonly DependencyProperty AllowPathDropProperty = DependencyProperty.Register(
        nameof(AllowPathDrop), typeof(bool), typeof(JobPathsPanel),
        new PropertyMetadata(false));

    public static readonly DependencyProperty RecentSourcesProperty = DependencyProperty.Register(
        nameof(RecentSources), typeof(IEnumerable), typeof(JobPathsPanel));

    public static readonly DependencyProperty RecentDestinationsProperty = DependencyProperty.Register(
        nameof(RecentDestinations), typeof(IEnumerable), typeof(JobPathsPanel));

    public static readonly DependencyProperty SourceFoldersProperty = DependencyProperty.Register(
        nameof(SourceFolders), typeof(IEnumerable), typeof(JobPathsPanel));

    public static readonly DependencyProperty CatcherTemplatesProperty = DependencyProperty.Register(
        nameof(CatcherTemplates), typeof(IEnumerable), typeof(JobPathsPanel));

    public static readonly DependencyProperty SelectedCatcherTemplateProperty = DependencyProperty.Register(
        nameof(SelectedCatcherTemplate), typeof(object), typeof(JobPathsPanel),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty BrowseSourceCommandProperty = DependencyProperty.Register(
        nameof(BrowseSourceCommand), typeof(ICommand), typeof(JobPathsPanel));

    public static readonly DependencyProperty AddSourceCommandProperty = DependencyProperty.Register(
        nameof(AddSourceCommand), typeof(ICommand), typeof(JobPathsPanel));

    public static readonly DependencyProperty ClearSourcesCommandProperty = DependencyProperty.Register(
        nameof(ClearSourcesCommand), typeof(ICommand), typeof(JobPathsPanel));

    public static readonly DependencyProperty RemoveSourceCommandProperty = DependencyProperty.Register(
        nameof(RemoveSourceCommand), typeof(ICommand), typeof(JobPathsPanel));

    public static readonly DependencyProperty BrowseDestCommandProperty = DependencyProperty.Register(
        nameof(BrowseDestCommand), typeof(ICommand), typeof(JobPathsPanel));

    public JobPathsPanel()
    {
        InitializeComponent();
        SourcePathCombo.PreviewDragOver += OnPathDragOver;
        SourcePathCombo.Drop += OnSourceDrop;
        DestPathCombo.PreviewDragOver += OnPathDragOver;
        DestPathCombo.Drop += OnDestDrop;
        CatcherPassphraseBox.PasswordChanged += (_, _) =>
            PassphraseChanged?.Invoke(this, CatcherPassphraseBox.Password);
        Loaded += OnLoadedAlignComboText;
    }

    public event DragEventHandler? SourceDrop;
    public event DragEventHandler? DestDrop;
    public event EventHandler<string>? PassphraseChanged;

    public PasswordBox PassphraseBox => CatcherPassphraseBox;

    public string SourcePath
    {
        get => (string)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public string DestPath
    {
        get => (string)GetValue(DestPathProperty);
        set => SetValue(DestPathProperty, value);
    }

    public int DestKindIndex
    {
        get => (int)GetValue(DestKindIndexProperty);
        set => SetValue(DestKindIndexProperty, value);
    }

    public bool DestIsFolder
    {
        get => (bool)GetValue(DestIsFolderProperty);
        set => SetValue(DestIsFolderProperty, value);
    }

    public bool DestIsCatcher
    {
        get => (bool)GetValue(DestIsCatcherProperty);
        set => SetValue(DestIsCatcherProperty, value);
    }

    public bool PathsEditable
    {
        get => (bool)GetValue(PathsEditableProperty);
        set => SetValue(PathsEditableProperty, value);
    }

    public bool HasSourceFolders
    {
        get => (bool)GetValue(HasSourceFoldersProperty);
        set => SetValue(HasSourceFoldersProperty, value);
    }

    public bool IncludeSourceFolderName
    {
        get => (bool)GetValue(IncludeSourceFolderNameProperty);
        set => SetValue(IncludeSourceFolderNameProperty, value);
    }

    public string LandingPreview
    {
        get => (string)GetValue(LandingPreviewProperty);
        set => SetValue(LandingPreviewProperty, value);
    }

    public bool ShowLandingPreview
    {
        get => (bool)GetValue(ShowLandingPreviewProperty);
        set => SetValue(ShowLandingPreviewProperty, value);
    }

    public bool ShowCatcherStatus
    {
        get => (bool)GetValue(ShowCatcherStatusProperty);
        set => SetValue(ShowCatcherStatusProperty, value);
    }

    public string CatcherOnlineStatus
    {
        get => (string)GetValue(CatcherOnlineStatusProperty);
        set => SetValue(CatcherOnlineStatusProperty, value);
    }

    public string CatcherTransferHint
    {
        get => (string)GetValue(CatcherTransferHintProperty);
        set => SetValue(CatcherTransferHintProperty, value);
    }

    public bool AllowPathDrop
    {
        get => (bool)GetValue(AllowPathDropProperty);
        set => SetValue(AllowPathDropProperty, value);
    }

    public IEnumerable? RecentSources
    {
        get => (IEnumerable?)GetValue(RecentSourcesProperty);
        set => SetValue(RecentSourcesProperty, value);
    }

    public IEnumerable? RecentDestinations
    {
        get => (IEnumerable?)GetValue(RecentDestinationsProperty);
        set => SetValue(RecentDestinationsProperty, value);
    }

    public IEnumerable? SourceFolders
    {
        get => (IEnumerable?)GetValue(SourceFoldersProperty);
        set => SetValue(SourceFoldersProperty, value);
    }

    public IEnumerable? CatcherTemplates
    {
        get => (IEnumerable?)GetValue(CatcherTemplatesProperty);
        set => SetValue(CatcherTemplatesProperty, value);
    }

    public object? SelectedCatcherTemplate
    {
        get => GetValue(SelectedCatcherTemplateProperty);
        set => SetValue(SelectedCatcherTemplateProperty, value);
    }

    public ICommand? BrowseSourceCommand
    {
        get => (ICommand?)GetValue(BrowseSourceCommandProperty);
        set => SetValue(BrowseSourceCommandProperty, value);
    }

    public ICommand? AddSourceCommand
    {
        get => (ICommand?)GetValue(AddSourceCommandProperty);
        set => SetValue(AddSourceCommandProperty, value);
    }

    public ICommand? ClearSourcesCommand
    {
        get => (ICommand?)GetValue(ClearSourcesCommandProperty);
        set => SetValue(ClearSourcesCommandProperty, value);
    }

    public ICommand? RemoveSourceCommand
    {
        get => (ICommand?)GetValue(RemoveSourceCommandProperty);
        set => SetValue(RemoveSourceCommandProperty, value);
    }

    public ICommand? BrowseDestCommand
    {
        get => (ICommand?)GetValue(BrowseDestCommandProperty);
        set => SetValue(BrowseDestCommandProperty, value);
    }

    private void OnLoadedAlignComboText(object sender, RoutedEventArgs e)
    {
        SourcePathCombo.AllowDrop = AllowPathDrop;
        DestPathCombo.AllowDrop = AllowPathDrop;
        AlignEditableText(SourcePathCombo);
        AlignEditableText(DestPathCombo);
        DestKindCombo.HorizontalContentAlignment = HorizontalAlignment.Left;
    }

    private static void AlignEditableText(ComboBox combo)
    {
        combo.HorizontalContentAlignment = HorizontalAlignment.Left;
        var box = FindDescendant<TextBox>(combo);
        if (box is null)
        {
            return;
        }

        box.TextAlignment = TextAlignment.Left;
        box.HorizontalContentAlignment = HorizontalAlignment.Left;
    }

    private void OnPathDragOver(object sender, DragEventArgs e)
    {
        if (!AllowPathDrop)
        {
            return;
        }

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnSourceDrop(object sender, DragEventArgs e)
    {
        if (!AllowPathDrop)
        {
            return;
        }

        SourceDrop?.Invoke(sender, e);
    }

    private void OnDestDrop(object sender, DragEventArgs e)
    {
        if (!AllowPathDrop)
        {
            return;
        }

        DestDrop?.Invoke(sender, e);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
