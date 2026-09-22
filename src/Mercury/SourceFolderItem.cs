namespace Mercury;

public sealed class SourceFolderItem
{
    public SourceFolderItem(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public string Name
    {
        get
        {
            var name = System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
            return string.IsNullOrWhiteSpace(name) ? Path : name;
        }
    }

    public override string ToString() => Path;
}
