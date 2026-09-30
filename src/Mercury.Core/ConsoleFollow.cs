namespace Mercury;

/// <summary>
/// Console Follow stays on when log lines arrive. It turns off only when the user scrolls away from the newest line.
/// </summary>
public static class ConsoleFollow
{
    public const double BottomSlack = 32;

    public static bool AfterScroll(
        bool following,
        bool programmatic,
        double extentHeightChange,
        double verticalChange,
        bool atBottom)
    {
        if (programmatic || extentHeightChange != 0)
        {
            return following;
        }

        if (verticalChange == 0)
        {
            return following;
        }

        return atBottom;
    }

    public static bool IsAtBottom(double verticalOffset, double scrollableHeight, double slack = BottomSlack) =>
        scrollableHeight <= 0 || verticalOffset + slack >= scrollableHeight;
}
