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

    [Fact]
    public void ToColumnsSplitsLabelFilesSizeAndShare()
    {
        var tb = 1024L * 1024 * 1024 * 1024;
        var video = (long)(2.93 * tb);
        var images = 451L * 1024 * 1024;
        var archives = (long)(1.60 * tb);
        var other = (long)(2.93 * 1024 * 1024 * 1024);
        var total = video + images + archives + other;

        var parts = new[]
        {
            TypeSummaryFormat.FormatPart("Video", 12632, video, total),
            TypeSummaryFormat.FormatPart("Images", 126, images, total),
            TypeSummaryFormat.FormatPart("Archives", 51514, archives, total),
            TypeSummaryFormat.FormatPart("Other", 103, other, total)
        };
        var rows = TypeSummaryFormat.ToColumns(parts);

        Assert.Equal(4, rows.Count);
        Assert.Equal(new TypeSummaryColumns("Video", "12,632 files", ByteFormatter.ToString(video), "65%"), rows[0]);
        Assert.Equal("Images", rows[1].Label);
        Assert.Equal("126 files", rows[1].Files);
        Assert.Equal(ByteFormatter.ToString(images), rows[1].Size);
        Assert.Equal("<1%", rows[1].Share);
        Assert.Equal("Archives", rows[2].Label);
        Assert.Equal("51,514 files", rows[2].Files);
        Assert.Equal(ByteFormatter.ToString(archives), rows[2].Size);
        Assert.Equal("35%", rows[2].Share);
        Assert.Equal("Other", rows[3].Label);
        Assert.Equal("103 files", rows[3].Files);
        Assert.Equal("<1%", rows[3].Share);
    }

    [Fact]
    public void ParseColumnsHandlesCompactAndPartialParts()
    {
        Assert.True(TypeSummaryFormat.TryParseColumns("Video: 12,632 files (82%)", out var compact));
        Assert.Equal(new TypeSummaryColumns("Video", "12,632 files", "", "82%"), compact);

        Assert.True(TypeSummaryFormat.TryParseColumns("Archives: 20 files", out var filesOnly));
        Assert.Equal(new TypeSummaryColumns("Archives", "20 files", "", ""), filesOnly);

        Assert.True(TypeSummaryFormat.TryParseColumns("Archives: 25443 files, 811 GB", out var noShare));
        Assert.Equal(new TypeSummaryColumns("Archives", "25443 files", "811 GB", ""), noShare);

        Assert.False(TypeSummaryFormat.TryParseColumns("  ", out _));
    }
}
