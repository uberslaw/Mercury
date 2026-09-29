using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Mercury;

public partial class ProgressStatsLine : UserControl
{
    public ProgressStatsLine()
    {
        InitializeComponent();
    }

    private void FilesAtOnceBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box)
        {
            return;
        }

        BindingOperations.GetBindingExpression(box, TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }
}
