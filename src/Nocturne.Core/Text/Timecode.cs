using System.Globalization;

namespace Nocturne.Core.Text;

/// <summary>
/// Formats and parses the timecodes shown in the transport bar.
/// </summary>
/// <remarks>
/// The player displays elapsed and total time side by side. Two rules keep that
/// pair readable while a file plays:
/// <list type="bullet">
/// <item>the elapsed side never changes width mid-playback, because a field that
/// grows from <c>9:59</c> to <c>10:00</c> shifts every glyph beside it;</item>
/// <item>both sides use the same shape, chosen from the longer of the two, so a
/// 90-minute film reads <c>01:02:03 / 01:31:00</c> rather than mixing forms.</item>
/// </list>
/// That is why callers should prefer <see cref="FormatPair"/> over two separate
/// <see cref="Format"/> calls.
/// </remarks>
public static class Timecode
{
    /// <summary>Longest value the player will render as a timecode.</summary>
    /// <remarks>
    /// libmpv reports <c>duration</c> as a double. A damaged container can yield
    /// an absurd or infinite value; clamping here stops the transport bar from
    /// rendering a thousand-hour field and stops seek math from overflowing.
    /// </remarks>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(99);

    /// <summary>
    /// Formats a single position, using hours only when the value needs them.
    /// </summary>
    public static string Format(TimeSpan value) => Format(value, forceHours: false);

    /// <summary>
    /// Formats a position, optionally forcing the <c>h:mm:ss</c> shape so it
    /// lines up with a longer companion value.
    /// </summary>
    public static string Format(TimeSpan value, bool forceHours)
    {
        TimeSpan clamped = Clamp(value);
        bool negative = clamped < TimeSpan.Zero;
        if (negative)
        {
            clamped = clamped.Negate();
        }

        // Round down: a position of 1.9s is still inside the second labelled 01,
        // and rounding up would let the elapsed field briefly display a value
        // greater than the duration at the very end of a file.
        long totalSeconds = (long)clamped.TotalSeconds;
        long hours = totalSeconds / 3600;
        long minutes = totalSeconds / 60 % 60;
        long seconds = totalSeconds % 60;

        string sign = negative ? "-" : string.Empty;
        return hours > 0 || forceHours
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{sign}{hours:D2}:{minutes:D2}:{seconds:D2}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{sign}{minutes:D2}:{seconds:D2}");
    }

    /// <summary>
    /// Formats an elapsed/total pair so both halves share one width.
    /// </summary>
    /// <returns>
    /// The two formatted halves. When <paramref name="duration"/> is unknown
    /// (zero or negative), the total half is <c>--:--</c> in the same shape as
    /// the elapsed half, which keeps live streams from collapsing the layout.
    /// </returns>
    public static (string Position, string Duration) FormatPair(TimeSpan position, TimeSpan duration)
    {
        TimeSpan clampedDuration = Clamp(duration);
        bool durationKnown = clampedDuration > TimeSpan.Zero;

        // The shape is chosen from the duration so it stays fixed for the whole
        // file instead of switching at the one-hour mark — but the position gets
        // a say too, by magnitude, and both of those matter.
        //
        // The position can exceed the duration: libmpv reports a pre-clamp
        // time-pos during a seek, and a wrong duration in a container is
        // ordinary. Ignoring the position then gives `02:00:00` beside `00:30`.
        // And Math.Abs, because a negative position also arrives from libmpv —
        // `-02:00:00` is three fields whatever its sign, while a comparison that
        // reads -2 as "less than one hour" pairs it with a two-field `--:--`.
        //
        // Both halves having the same number of fields is the entire reason this
        // method exists in place of two Format calls.
        bool forceHours =
            (durationKnown && clampedDuration.TotalHours >= 1)
            || Math.Abs(Clamp(position).TotalHours) >= 1;

        string formattedPosition = Format(position, forceHours);
        string formattedDuration = durationKnown
            ? Format(clampedDuration, forceHours)
            : forceHours ? "--:--:--" : "--:--";

        return (formattedPosition, formattedDuration);
    }

    /// <summary>
    /// Clamps a raw engine value into the range the player is willing to show.
    /// </summary>
    /// <remarks>
    /// Non-finite doubles arrive from libmpv for streams with no known duration.
    /// </remarks>
    public static TimeSpan FromSeconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return TimeSpan.Zero;
        }

        // Clamp while the value is still a double. TimeSpan.FromSeconds throws
        // OverflowException for magnitudes it cannot represent, so constructing
        // first and clamping afterwards never gets the chance to clamp.
        double limitSeconds = MaximumDuration.TotalSeconds;
        if (seconds > limitSeconds)
        {
            return MaximumDuration;
        }

        return seconds < -limitSeconds ? MaximumDuration.Negate() : TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan Clamp(TimeSpan value)
    {
        if (value > MaximumDuration)
        {
            return MaximumDuration;
        }

        TimeSpan lowerBound = MaximumDuration.Negate();
        return value < lowerBound ? lowerBound : value;
    }
}
