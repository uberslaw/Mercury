using System.Diagnostics;
using System.IO;
using System.Windows.Input;

namespace Mercury;

public sealed class SettingsPathItem
{
    public SettingsPathItem(DataPathRow row)
    {
        Label = row.Label;
        FullPath = row.FullPath;
        Folder = row.Folder;
        OpenFolderCommand = new RelayCommand(OpenFolder);
    }

    public string Label { get; }
    public string FullPath { get; }
    public string Folder { get; }
    public ICommand OpenFolderCommand { get; }

    private void OpenFolder()
    {
        var target = Folder;
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        Directory.CreateDirectory(target);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "\"" + target + "\"",
            UseShellExecute = true
        });
    }
}
