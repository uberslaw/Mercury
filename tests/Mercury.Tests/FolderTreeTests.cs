namespace Mercury.Tests;

public class FolderTreeTests
{
    [Fact]
    public void BuildsFolderRowsNotFileNodes()
    {
        var nodes = FolderTree.Build(
            [
                Rec(@"a\b\c.txt", 100, FileCopyStatus.Pending),
                Rec(@"a\d.txt", 100, FileCopyStatus.Copied),
                Rec(@"e\f.txt", 50, FileCopyStatus.Unpacked)
            ],
            bytesPerSecond: 50);

        Assert.Equal(2, nodes.Count);
        Assert.Equal("a", nodes[0].Name);
        Assert.Equal("e", nodes[1].Name);
        Assert.Equal(2, nodes[0].FileCount);
        Assert.Equal(1, nodes[0].SubdirCount);
        Assert.Equal("50%", nodes[0].PercentText);
        Assert.Equal("2s", nodes[0].EtaText);
        Assert.Single(nodes[0].Children);
        Assert.Equal("b", nodes[0].Children[0].Name);
        Assert.Equal(0, nodes[0].Children[0].SubdirCount);
        Assert.Equal("0%", nodes[0].Children[0].PercentText);
        Assert.Equal("2s", nodes[0].Children[0].EtaText);
        Assert.Equal(1, nodes[1].FileCount);
        Assert.Equal(0, nodes[1].SubdirCount);
        Assert.Equal("100%", nodes[1].PercentText);
        Assert.Equal("—", nodes[1].EtaText);
        Assert.Empty(nodes[0].Children[0].Children);
    }

    [Fact]
    public void LeafSourceWithOnlyRootFilesIsOneRow()
    {
        var nodes = FolderTree.Build(
            [
                Rec("one.txt", 10, FileCopyStatus.Copied),
                Rec("two.txt", 10, FileCopyStatus.Pending)
            ],
            bytesPerSecond: 0,
            rootName: "photos");

        var leaf = Assert.Single(nodes);
        Assert.Equal("photos", leaf.Name);
        Assert.Equal(2, leaf.FileCount);
        Assert.Equal(0, leaf.SubdirCount);
        Assert.Equal("50%", leaf.PercentText);
        Assert.Equal("—", leaf.EtaText);
    }

    [Fact]
    public void EmptyInputIsEmpty()
    {
        Assert.Empty(FolderTree.Build([], 100));
    }

    [Fact]
    public void SkippedCountsAsDoneAndEtaDashUntilSpeed()
    {
        var nodes = FolderTree.Build(
            [Rec(@"day1\a.jpg", 200, FileCopyStatus.Skipped)],
            bytesPerSecond: 0);
        var day = Assert.Single(nodes);
        Assert.Equal("100%", day.PercentText);
        Assert.Equal("—", day.EtaText);
    }

    private static FileRecord Rec(string relative, long size, FileCopyStatus status) =>
        new()
        {
            RelativePath = relative,
            Size = size,
            Status = status
        };
}
