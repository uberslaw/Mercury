using System.Globalization;

namespace Mercury;

/// <summary>
/// Progress Types line: file counts plus each kind’s share of total bytes.
/// Collapsed display fits one line; leftover kinds expand on demand.
/// </summary>
public static class TypeSummaryFormat
{
    public const string PartSeparator = "; ";
    public const string MoreLabel = "more";
    public const string LessLabel = "less";

    public static string FormatPart(string label, int files, long bytes, long totalBytes)
    {
        var count = files.ToString("N0", CultureInfo.InvariantCulture);
        return $"{label}: {count} files, {ByteFormatter.ToString(bytes)} ({FormatByteShare(bytes, totalBytes)})";
    }

    public static string FormatByteShare(long bytes, long totalBytes)
    {
        if (totalBytes <= 0 || bytes <= 0)
        {
            return "0%";
        }

        var pct = 100d * bytes / totalBytes;
        if (pct < 1d)
        {
            return "<1%";
        }

        var rounded = (int)Math.Round(pct, MidpointRounding.AwayFromZero);
        if (rounded > 100)
        {
            rounded = 100;
        }

        return rounded.ToString(CultureInfo.InvariantCulture) + "%";
    }

    public static IReadOnlyList<string> SplitParts(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return [];
        }

        return summary.Split(
            PartSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string JoinParts(IReadOnlyList<string> parts, bool expanded)
    {
        if (parts.Count == 0)
        {
            return "";
        }

        return expanded
            ? string.Join(Environment.NewLine, parts)
            : string.Join(PartSeparator, parts);
    }

    public static string WithoutSize(string part)
    {
        if (!TryHeadAndShare(part, out var head, out var share))
        {
            return part;
        }

        const string filesToken = " files, ";
        var i = head.IndexOf(filesToken, StringComparison.Ordinal);
        if (i < 0)
        {
            return part;
        }

        return head[..i] + " files (" + share + ")";
    }

    public static string CountAndShare(string part)
    {
        if (!TryHeadAndShare(part, out var head, out var share))
        {
            return part;
        }

        const string filesToken = " files, ";
        var i = head.IndexOf(filesToken, StringComparison.Ordinal);
        if (i < 0)
        {
            return part;
        }

        return head[..i] + " (" + share + ")";
    }

    public static string LabelAndShare(string part)
    {
        if (!TryHeadAndShare(part, out var head, out var share))
        {
            return part;
        }

        var colon = head.IndexOf(':');
        var label = colon > 0 ? head[..colon] : head;
        return label + " (" + share + ")";
    }

    /// <summary>
    /// Longest one-line Types text that fits <paramref name="available"/>.
    /// Complete type clauses only — never ellipsis.
    /// </summary>
    public static (string Text, bool NeedsExpand) FitOneLine(
        IReadOnlyList<string> parts,
        Func<string, double> widthOf,
        double available)
    {
        if (parts.Count == 0)
        {
            return ("", false);
        }

        var full = JoinParts(parts, expanded: false);
        if (widthOf(full) <= available)
        {
            return (full, false);
        }

        var fitted = new List<string>();
        foreach (var part in parts)
        {
            var candidate = fitted.Count == 0
                ? part
                : string.Join(PartSeparator, fitted) + PartSeparator + part;
            if (widthOf(candidate) <= available)
            {
                fitted.Add(part);
            }
            else
            {
                break;
            }
        }

        if (fitted.Count > 0)
        {
            return (string.Join(PartSeparator, fitted), true);
        }

        var compact = WithoutSize(parts[0]);
        if (widthOf(compact) <= available)
        {
            return (compact, true);
        }

        var counted = CountAndShare(parts[0]);
        if (widthOf(counted) <= available)
        {
            return (counted, true);
        }

        return (LabelAndShare(parts[0]), true);
    }

    private static bool TryHeadAndShare(string part, out string head, out string share)
    {
        head = part;
        share = "";
        if (!part.EndsWith(')'))
        {
            return false;
        }

        var open = part.LastIndexOf(" (", StringComparison.Ordinal);
        if (open <= 0)
        {
            return false;
        }

        share = part[(open + 2)..^1];
        head = part[..open];
        return share.Length > 0;
    }
}
