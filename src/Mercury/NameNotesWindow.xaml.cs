using System.Windows;

namespace Mercury;

public partial class NameNotesWindow : Window
{
    public NameNotesWindow(Job job)
    {
        InitializeComponent();
        var notes = job.NameNotices ?? [];
        Title = notes.Count == 1 ? "1 name note" : notes.Count + " name notes";
        SummaryText.Text = notes.Count == 0
            ? "No long or duplicate destination names were recorded for this job."
            : notes.Count + " long or duplicate destination name(s). Each line is also in the Console log.";
        NotesList.ItemsSource = notes;
    }

    public static void Show(Window? owner, Job job)
    {
        var window = new NameNotesWindow(job) { Owner = owner };
        window.ShowDialog();
    }
}
