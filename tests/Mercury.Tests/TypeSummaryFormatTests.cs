namespace Mercury.Tests;

public class TypeSummaryFormatTests
{
    [Fact]
    public void FormatPartUsesCountSizeAndByteShare()
    {
        var tb = 1024L * 1024 * 1024 * 1024;
        var video = (long)(2.93 * tb);
        var total = (long)Math.Round(video * 100d / 82d);
        var part = TypeSummaryFormat.FormatPart("Video", 12632, video, total);
        Assert.Equal($"Video: 12,632 files, {ByteFormatter.ToString(video)} ({TypeSummaryFormat.FormatByteShare(video, total)})", part);
        Assert.Contains("12,632 files", part, StringComparison.Ordinal);
        Assert.Contains("(82%)", part, StringComparison.Ordinal);
    }

    [Fact]
    public void TinyShareShowsLessThanOnePercent()
    {
        Assert.Equal("<1%", TypeSummaryFormat.FormatByteShare(1, 1000));
        Assert.Equal("0%", TypeSummaryFormat.FormatByteShare(0, 1000));
        Assert.Equal("100%", TypeSummaryFormat.FormatByteShare(50, 50));
    }

    [Fact]
    public void FitOneLineKeepsWholeStringWhenItFits()
    {
        string[] parts =
        [
            "Video: 12,632 files, 2.93 TB (82%)",
            "Images: 126 files, 451 MB (<1%)"
        ];
        var fitted = TypeSummaryFormat.FitOneLine(parts, s => s.Length, 1000);
        Assert.False(fitted.NeedsExpand);
        Assert.Equal(string.Join("; ", parts), fitted.Text);
        Assert.DoesNotContain("...", fitted.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FitOneLineDropsTrailingPartsInsteadOfEllipsis()
    {
        string[] parts =
        [
            "Video: 12,632 files, 2.93 TB (82%)",
            "Images: 126 files, 451 MB (<1%)",
            "Archives: 515 files, 120 GB (3%)"
        ];
        var first = parts[0];
        var fitted = TypeSummaryFormat.FitOneLine(parts, s => s.Length, first.Length);
        Assert.True(fitted.NeedsExpand);
        Assert.Equal(first, fitted.Text);
        Assert.DoesNotContain("...", fitted.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Images", fitted.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FitOneLineCompactsFirstPartWhenEvenOneTypeIsTooWide()
    {
        var part = "Video: 12,632 files, 2.93 TB (82%)";
        var compact = TypeSummaryFormat.WithoutSize(part);
        Assert.Equal("Video: 12,632 files (82%)", compact);
        Assert.Equal("Video: 12,632 (82%)", TypeSummaryFormat.CountAndShare(part));
        Assert.Equal("Video (82%)", TypeSummaryFormat.LabelAndShare(part));

        var fitted = TypeSummaryFormat.FitOneLine([part], s => s.Length, compact.Length);
        Assert.True(fitted.NeedsExpand);
        Assert.Equal(compact, fitted.Text);

        var shortFit = TypeSummaryFormat.FitOneLine([part], s => s.Length, 12);
        Assert.True(shortFit.NeedsExpand);
        Assert.Equal("Video (82%)", shortFit.Text);
    }

    [Fact]
    public void ExpandedJoinPutsEachTypeOnItsOwnLine()
    {
        string[] parts = ["Video: 1 files, 1 B (50%)", "Images: 1 files, 1 B (50%)"];
        var expanded = TypeSummaryFormat.JoinParts(parts, expanded: true);
        Assert.Contains('\n', expanded);
        Assert.DoesNotContain("...", expanded, StringComparison.Ordinal);
        Assert.Contains("Images:", expanded, StringComparison.Ordinal);
    }
}
