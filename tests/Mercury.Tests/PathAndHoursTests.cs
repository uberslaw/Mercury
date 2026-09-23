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
            Assert.Equal(mapping.DestRoot, CopyShape.LandingPath(mapping), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(src, true);
            Directory.Delete(dest, true);
        }
    }

    [Fact]
    public void FolderContentsOnlyWhenIncludeSourceFolderNameOff()
    {
        var src = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mercury-shape-src-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var dest = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mercury-shape-dst-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var mapping = CopyShape.Resolve(src, dest, includeSourceFolderName: false);
            Assert.Equal(SourceKind.Folder, mapping.Kind);
            Assert.Equal(dest, mapping.DestRoot, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(src, true);
            Directory.Delete(dest, true);
        }
    }

    [Fact]
    public void PreviewLandingPathWrapsAnchorSpanFolder()
    {
        var dest = @"Z:\EngA data drive";
        var wrapped = CopyShape.PreviewLandingPath(@"D:\Anchor Span", dest, includeSourceFolderName: true);
        Assert.Equal(Path.Combine(dest, "Anchor Span"), wrapped);
        var contents = CopyShape.PreviewLandingPath(@"D:\Anchor Span", dest, includeSourceFolderName: false);
        Assert.Equal(dest, contents);
        Assert.True(new JobOptions().IncludeSourceFolderName);
    }

    [Fact]
    public void DriveRootDumpsContentsIntoDest()
    {
        var dest = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mercury-shape-drv-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var mapping = CopyShape.Resolve(@"C:\", dest);
            Assert.Equal(SourceKind.DriveRoot, mapping.Kind);
            Assert.False(mapping.SingleFile);
            Assert.Equal(dest, mapping.DestRoot, ignoreCase: true);
            Assert.Equal(dest, CopyShape.PreviewLandingPath(@"C:\", dest, includeSourceFolderName: true), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(dest, true);
        }
    }

    [Fact]
    public void FolderOntoDriveRootKeepsSeparator()
    {
        var src = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mercury-shape-root-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var mapping = CopyShape.Resolve(src, @"C:\");
            var expected = Path.Combine(@"C:\", Path.GetFileName(src));
            Assert.Equal(expected, mapping.DestRoot, ignoreCase: true);
            Assert.StartsWith(@"C:\", mapping.DestRoot, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(src, true);
        }
    }

    [Fact]
    public void NestedFilesLandUnderSourceFolderName()
    {
        var root = Path.Combine(Path.GetTempPath(), "mercury-nest-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var dest = Path.Combine(root, "dest");
        Directory.CreateDirectory(Path.Combine(src, "A", "B"));
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(src, "A", "B", "c.txt"), "x");
        try
        {
            var wrapped = CopyShape.Resolve(src, dest);
            var wrappedFiles = SourceWalker.Walk(wrapped).ToList();
            Assert.Single(wrappedFiles);
            Assert.Equal(Path.Combine("A", "B", "c.txt"), wrappedFiles[0].RelativePath);
            Assert.Equal(Path.Combine(dest, "src", "A", "B", "c.txt"), wrappedFiles[0].DestPath, ignoreCase: true);

            var contents = CopyShape.Resolve(src, dest, includeSourceFolderName: false);
            var contentFiles = SourceWalker.Walk(contents).ToList();
            Assert.Single(contentFiles);
            Assert.Equal(Path.Combine(dest, "A", "B", "c.txt"), contentFiles[0].DestPath, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PreviewShowsNestedFolderWhenDestIsInsidePreviousCopy()
    {
        var preview = CopyShape.PreviewLandingPath(
            @"D:\story bridge",
            @"X:\x\story bridge photos",
            includeSourceFolderName: true);
        Assert.Equal(@"X:\x\story bridge photos\story bridge", preview, ignoreCase: true);
    }

    [Fact]
    public void PreviewDriveRootAndFile()
    {
        Assert.Equal(
            @"E:\backup",
            CopyShape.PreviewLandingPath(@"D:\", @"E:\backup"),
            ignoreCase: true);

        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mercury-prev-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var dest = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mercury-prev-dst-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        var file = Path.Combine(dir, "note.txt");
        File.WriteAllText(file, "hi");
        try
        {
            Assert.Equal(Path.Combine(dest, "note.txt"), CopyShape.PreviewLandingPath(file, dest), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(dir, true);
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

    [Fact]
    public void CombineDoesNotLetRootedRelativeDropTheLanding()
    {
        var dest = Path.Combine(Path.GetTempPath(), "mercury-combine-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dest);
        try
        {
            var landing = Path.Combine(dest, "Anchor Span");
            Directory.CreateDirectory(landing);
            var path = PathNormalizer.Combine(landing, @"compressed\a.zip");
            Assert.Equal(Path.Combine(landing, "compressed", "a.zip"), path, ignoreCase: true);
            var escaped = PathNormalizer.Combine(landing, Path.Combine(dest, "compressed", "a.zip"));
            Assert.True(
                PathNormalizer.IsUnder(escaped, landing)
                || escaped.StartsWith(landing, StringComparison.OrdinalIgnoreCase),
                escaped);
        }
        finally
        {
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
