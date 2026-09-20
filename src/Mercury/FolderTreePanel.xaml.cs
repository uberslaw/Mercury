using System.Windows.Controls;

namespace Mercury;

public partial class FolderTreePanel : UserControl
{
    public FolderTreePanel()
    {
        InitializeComponent();
    }

    internal Border Header => HeaderBand;
    internal TreeView Tree => FolderTreeView;
}

