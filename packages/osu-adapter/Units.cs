using System.Globalization;

namespace OsuAi.OsuAdapter;

internal static class Units
{
    public static long MsToUs(double ms) => checked((long)Math.Round(ms * 1000, MidpointRounding.AwayFromZero));

    public static double UsToMs(long us) => us / 1000.0;

    /// <summary>
    /// The official model stores many values as float. Widening 5.3f directly gives 5.300000190734863;
    /// going through the shortest round-trip string gives 5.3, which narrows back to the same float.
    /// </summary>
    public static double FromFloat(float value) => double.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
