using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Mercury;

public sealed class FolderTreeItem : INotifyPropertyChanged
{
    private readonly string _recursiveFilesText;
    private readonly string _directFilesText;
    private readonly string _subtreePercentText;
    private readonly string _directPercentText;
    private readonly string _subtreeEtaText;
    private readonly string _directEtaText;
    private readonly int _directFileCount;
    private bool _isExpanded;

    public FolderTreeItem(FolderTreeNode node, ISet<string> expanded)
    {
        Name = node.Name;
        RelativePath = node.RelativePath;
        _recursiveFilesText = node.FilesText;
        _directFilesText = node.DirectFilesText;
        _directFileCount = node.DirectFileCount;
        _subtreePercentText = node.PercentText;
        _directPercentText = node.DirectPercentText;
        _subtreeEtaText = node.EtaText;
        _directEtaText = node.DirectEtaText;
        SubdirsText = node.IsLeaf ? "0" : node.SubdirsText;
        IsLeaf = node.IsLeaf;
        foreach (var child in node.Children)
        {
            Children.Add(new FolderTreeItem(child, expanded));
        }

        _isExpanded = expanded.Contains(node.RelativePath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string RelativePath { get; }
    public string FilesText => UseDirectCounts ? _directFilesText : _recursiveFilesText;
    public string SubdirsText { get; }
    public string PercentText => UseDirectCounts && _directFileCount == 0 ? "—" : UseDirectCounts ? _directPercentText : _subtreePercentText;
    public string EtaText => UseDirectCounts && _directFileCount == 0 ? "—" : UseDirectCounts ? _directEtaText : _subtreeEtaText;
    public bool IsLeaf { get; }
    public bool HasChildren => Children.Count > 0;
    public double IndentWidth
    {
        get
        {
            var depth = 0;
            foreach (var c in RelativePath)
            {
                if (c is '\\' or '/')
                {
                    depth++;
                }
            }

            return depth * 16;
        }
    }
    public ObservableCollection<FolderTreeItem> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilesText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PercentText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EtaText)));
        }
    }

    private bool UseDirectCounts => _isExpanded && HasChildren;

    public static void CollectExpanded(IEnumerable<FolderTreeItem> items, ISet<string> expanded)
    {
        foreach (var item in items)
        {
            if (item.IsExpanded && !string.IsNullOrEmpty(item.RelativePath))
            {
                expanded.Add(item.RelativePath);
            }

            CollectExpanded(item.Children, expanded);
        }
    }
}
