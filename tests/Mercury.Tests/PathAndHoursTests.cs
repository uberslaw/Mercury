namespace Mercury.Tests;

public class PathNormalizerTests
{
    [Fact]
    public void DriveLetterBecomesRoot()
    {
        var path = PathNormalizer.Normalize("C:");
        Assert.Equal(@"C:\", path, ignoreCase: true);
    }

    [Fact]
    public void IsDriveRoot_ForDrive()
    {
        Assert.True(PathNormalizer.IsDriveRoot(@"C:\"));
        Assert.False(PathNormalizer.IsDriveRoot(@"C:\Windows"));
    }

    [Fact]
    public void IsUnder_UsesTrailingSeparator()
    {
        Assert.True(PathNormalizer.IsUnder(@"C:\backup\file.txt", @"C:\backup"));
        Assert.False(PathNormalizer.IsUnder(@"C:\backup2\file.txt", @"C:\backup"));
    }
}

public class CopyShapeTests
{
    [Fact]
    public void FolderCopiesAsChildOfDest()
    {
        var src = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mercury-shape-src-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var dest = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mercury-shape-dst-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var mapping = CopyShape.Resolve(src, dest);
            Assert.Equal(SourceKind.Folder, mapping.Kind);
            Assert.Equal(Path.Combine(dest, Path.GetFileName(src)), mapping.DestRoot, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(src, true);
            Directory.Delete(dest, true);
        }
    }

    [Fact]
    public void FileIntoFolderKeepsName()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mercury-file-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var dest = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mercury-file-dst-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var file = Path.Combine(dir, "note.txt");
        File.WriteAllText(file, "hi");
        try
        {
            var mapping = CopyShape.Resolve(file, dest);
            Assert.True(mapping.SingleFile);
            Assert.Equal("note.txt", mapping.SingleFileName);
            Assert.Equal(dest, mapping.DestRoot, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(dir, true);
            Directory.Delete(dest, true);
        }
    }
}

public class RunWindowTests
{
    [Fact]
    public void DaytimeWindow()
    {
        Assert.True(RunWindow.IsInside(new TimeOnly(10, 0), new TimeOnly(8, 0), new TimeOnly(18, 0)));
        Assert.False(RunWindow.IsInside(new TimeOnly(19, 0), new TimeOnly(8, 0), new TimeOnly(18, 0)));
        Assert.False(RunWindow.IsInside(new TimeOnly(7, 59), new TimeOnly(8, 0), new TimeOnly(18, 0)));
    }

    [Fact]
    public void OvernightWindow()
    {
        Assert.True(RunWindow.IsInside(new TimeOnly(22, 30), new TimeOnly(22, 0), new TimeOnly(6, 0)));
        Assert.True(RunWindow.IsInside(new TimeOnly(1, 0), new TimeOnly(22, 0), new TimeOnly(6, 0)));
        Assert.False(RunWindow.IsInside(new TimeOnly(12, 0), new TimeOnly(22, 0), new TimeOnly(6, 0)));
    }

    [Fact]
    public void DelayUntilOpen_IsZeroInside()
    {
        Assert.Equal(TimeSpan.Zero, RunWindow.DelayUntilOpen(new TimeOnly(10, 0), new TimeOnly(8, 0), new TimeOnly(18, 0)));
        Assert.True(RunWindow.DelayUntilOpen(new TimeOnly(19, 0), new TimeOnly(8, 0), new TimeOnly(18, 0)) > TimeSpan.Zero);
    }
}
