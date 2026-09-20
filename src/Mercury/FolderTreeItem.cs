using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Mercury;

public sealed class FolderTreeItem : INotifyPropertyChanged
{
    private bool _isExpanded;

    public FolderTreeItem(FolderTreeNode node, ISet<string> expanded)
    {
        Name = node.Name;
        RelativePath = node.RelativePath;
        FilesText = node.FilesText;
        SubdirsText = node.IsLeaf ? "0" : node.SubdirsText;
        PercentText = node.PercentText;
        EtaText = node.EtaText;
        IsLeaf = node.IsLeaf;
        _isExpanded = expanded.Contains(node.RelativePath);
        foreach (var child in node.Children)
        {
            Children.Add(new FolderTreeItem(child, expanded));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string RelativePath { get; }
    public string FilesText { get; }
    public string SubdirsText { get; }
    public string PercentText { get; }
    public string EtaText { get; }
    public bool IsLeaf { get; }
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
        }
    }

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
