using System.Windows;

namespace Mercury;

public partial class JobOptionsWindow : Window
{
    public JobOptionsWindow(JobOptionsForm form)
    {
        InitializeComponent();
        Bind(form);
        Closed += (_, _) =>
        {
            if (DataContext is JobOptionsForm current)
            {
                current.CloseRequested -= OnFormCloseRequested;
            }
        };
        Closing += (_, _) =>
        {
            if (DataContext is JobOptionsForm form)
            {
                form.ApplyCommand.Execute(null);
            }
        };
    }

    public void Bind(JobOptionsForm form)
    {
        if (DataContext is JobOptionsForm previous)
        {
            previous.CloseRequested -= OnFormCloseRequested;
        }

        DataContext = form;
        form.CloseRequested += OnFormCloseRequested;
        Title = form.WindowTitle;
    }

    private void OnFormCloseRequested()
    {
        try
        {
            Close();
        }
        catch
        {
            // already closing
        }
    }
}
