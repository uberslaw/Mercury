using System.Globalization;

namespace Mercury;

/// <summary>
/// Display unit for bandwidth boxes and Progress Speed.
/// Stored values stay megabytes/second. 1 MB/s = 8 Mbps (as labelled).
/// </summary>
public static class BandwidthUnit
{
    public const double MegabitsPerMegabyte = 8;

    public static string Label(bool megabits) => megabits ? "Mbps" : "MB/s";

    public static double ToDisplay(double megabytesPerSecond, bool megabits) =>
        megabits ? megabytesPerSecond * MegabitsPerMegabyte : megabytesPerSecond;

    public static double FromDisplay(double displayed, bool megabits) =>
        megabits ? displayed / MegabitsPerMegabyte : displayed;

    public static string FormatMegabytes(double megabytesPerSecond, bool megabits) =>
        ToDisplay(megabytesPerSecond, megabits).ToString("0.###", CultureInfo.InvariantCulture);

    public static bool TryParseMegabytes(string? text, bool megabits, out double megabytesPerSecond)
    {
        megabytesPerSecond = 0;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var displayed) || displayed <= 0)
        {
            return false;
        }

        megabytesPerSecond = FromDisplay(displayed, megabits);
        return megabytesPerSecond > 0;
    }

    public static string ConvertDisplayText(string text, bool fromMegabits, bool toMegabits)
    {
        if (fromMegabits == toMegabits || string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return TryParseMegabytes(text, fromMegabits, out var mb)
            ? FormatMegabytes(mb, toMegabits)
            : text;
    }
}
