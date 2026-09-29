using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Mercury;

public partial class DestFilesWindow : Window
{
    private readonly Job _job;
    private readonly string _journalDirectory;
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    public DestFilesWindow(Job job, string journalDirectory)
    {
        _job = job;
        _journalDirectory = journalDirectory;
        InitializeComponent();
        FileList.SelectionChanged += (_, _) => LoadSelection();
        Reload();
    }

    public static void Show(Window? owner, Job job, string journalDirectory)
    {
        var window = new DestFilesWindow(job, journalDirectory)
        {
            Owner = owner,
            Title = "Copied files — " + (string.IsNullOrWhiteSpace(job.Name) ? job.Id[..Math.Min(8, job.Id.Length)] : job.Name)
        };
        window.ShowDialog();
    }

    private void Reload()
    {
        string? selected = (FileList.SelectedItem as Row)?.RelativePath;
        List<Row> rows;
        try
        {
            using var journal = JobJournal.Open(_journalDirectory);
            rows = journal.GetFiles()
                .OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(f => new Row(f))
                .ToList();
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            return;
        }

        FileList.ItemsSource = rows;
        if (rows.Count == 0)
        {
            ErrorText.Text = "This job has no files in its journal.";
            return;
        }

        var match = selected is null
            ? rows[0]
            : rows.FirstOrDefault(r => string.Equals(r.RelativePath, selected, StringComparison.OrdinalIgnoreCase)) ?? rows[0];
        FileList.SelectedItem = match;
        FileList.ScrollIntoView(match);
    }

    private void LoadSelection()
    {
        if (FileList.SelectedItem is not Row row)
        {
            return;
        }

        NameBox.Text = row.FileName;
        TimeBox.Text = row.LastWriteLocal.ToString(TimeFormat, CultureInfo.InvariantCulture);
        ErrorText.Text = "";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not Row row)
        {
            ErrorText.Text = "Select a file first.";
            return;
        }

        if (!DateTime.TryParseExact(
                TimeBox.Text.Trim(),
                TimeFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var local))
        {
            ErrorText.Text = "Last-write time must look like 2026-09-29 14:30:00.";
            return;
        }

        try
        {
            using var journal = JobJournal.Open(_journalDirectory);
            var result = DestFileEdit.Apply(_job, journal, row.RelativePath, NameBox.Text, local);
            if (!result.Ok)
            {
                ErrorText.Text = result.Error ?? "Could not update that file.";
                return;
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            return;
        }

        var updated = NameBox.Text.Trim();
        Reload();
        ErrorText.Text = "Updated " + updated + ".";
    }

    private sealed class Row
    {
        public Row(FileRecord file)
        {
            RelativePath = file.RelativePath;
            var name = file.RelativePath ?? "";
            var slash = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
            FileName = slash < 0 ? name : name[(slash + 1)..];
            var utc = file.LastWriteUtc.Kind == DateTimeKind.Utc
                ? file.LastWriteUtc
                : DateTime.SpecifyKind(file.LastWriteUtc, DateTimeKind.Utc);
            LastWriteLocal = utc.ToLocalTime();
        }

        public string RelativePath { get; }
        public string FileName { get; }
        public DateTime LastWriteLocal { get; }
        public string Display => RelativePath;
    }
}
