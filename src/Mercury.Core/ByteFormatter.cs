using System.Globalization;

namespace Mercury;

public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string ToString(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + ToString(-bytes);
        }

        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string Speed(double bytesPerSecond)
    {
        if (bytesPerSecond < 0)
        {
            bytesPerSecond = 0;
        }

        return ToString((long)bytesPerSecond) + "/s";
    }

    public static string Eta(TimeSpan? t)
    {
        if (t is null)
        {
            return "—";
        }

        var value = t.Value;
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}h {value.Minutes:D2}m";
        }

        if (value.TotalMinutes >= 1)
        {
            return $"{value.Minutes}m {value.Seconds:D2}s";
        }

        return $"{Math.Max(0, (int)value.TotalSeconds)}s";
    }

    public static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}h {value.Minutes:D2}m {value.Seconds:D2}s";
        }

        if (value.TotalMinutes >= 1)
        {
            return $"{value.Minutes}m {value.Seconds:D2}s";
        }

        return $"{Math.Max(0, (int)Math.Round(value.TotalSeconds))}s";
    }

    /// <summary>
    /// Instantaneous rate, or bytes/elapsed after a short warmup. Zero only in the first few seconds.
    /// </summary>
    public static double EffectiveRate(double instantaneous, long bytesCopied, TimeSpan elapsed)
    {
        if (instantaneous >= 1)
        {
            return instantaneous;
        }

        if (bytesCopied > 0 && elapsed.TotalSeconds >= 3)
        {
            return bytesCopied / elapsed.TotalSeconds;
        }

        return 0;
    }

    public static string CompactEta(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        if (value.TotalHours >= 1)
        {
            return $"~{(int)value.TotalHours}h {value.Minutes}m";
        }

        if (value.TotalMinutes >= 1)
        {
            return $"~{(int)Math.Round(value.TotalMinutes)}m";
        }

        return $"~{Math.Max(1, (int)Math.Round(value.TotalSeconds))}s";
    }

    public static string AboutDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        if (value.TotalHours >= 1.5)
        {
            return $"about {(int)Math.Round(value.TotalHours)} hours";
        }

        if (value.TotalHours >= 1)
        {
            return "about an hour";
        }

        if (value.TotalMinutes >= 1.5)
        {
            return $"about {(int)Math.Round(value.TotalMinutes)} minutes";
        }

        if (value.TotalMinutes >= 1)
        {
            return "about a minute";
        }

        return $"about {Math.Max(1, (int)Math.Round(value.TotalSeconds))} seconds";
    }
}
