using System.Globalization;

namespace Coanda.Daemon;

/// <summary>How a limit reads in a reason stored on a flow run.</summary>
public static class Durations
{
    /// <summary>Whole minutes as minutes, anything else as seconds.</summary>
    public static string Describe(TimeSpan limit) =>
        limit >= TimeSpan.FromMinutes(1) && limit.Seconds == 0 && limit.Milliseconds == 0
            ? $"{limit.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes"
            : $"{limit.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds";
}
