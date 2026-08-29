using Nocturne.Core.Text;
using Xunit;

namespace Nocturne.Core.Tests.Text;

public sealed class TimecodeTests
{
    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(9, "00:09")]
    [InlineData(102, "01:42")]
    [InlineData(268, "04:28")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "01:00:00")]
    [InlineData(3723, "01:02:03")]
    public void Format_uses_hours_only_when_needed(double seconds, string expected) =>
        Assert.Equal(expected, Timecode.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Format_truncates_rather_than_rounding()
    {
        // 1.9s is still inside second 1. Rounding up would let the elapsed field
        // read one second past the duration at the end of a file.
        Assert.Equal("00:01", Timecode.Format(TimeSpan.FromSeconds(1.9)));
    }

    [Fact]
    public void FormatPair_widens_position_to_match_a_long_duration()
    {
        var (position, duration) = Timecode.FormatPair(
            TimeSpan.FromSeconds(102),
            TimeSpan.FromMinutes(91));

        Assert.Equal("00:01:42", position);
        Assert.Equal("01:31:00", duration);
    }

    [Fact]
    public void FormatPair_keeps_the_short_shape_for_short_files()
    {
        var (position, duration) = Timecode.FormatPair(
            TimeSpan.FromSeconds(102),
            TimeSpan.FromSeconds(268));

        Assert.Equal("01:42", position);
        Assert.Equal("04:28", duration);
    }

    [Fact]
    public void FormatPair_reports_an_unknown_duration_without_collapsing_the_layout()
    {
        var (position, duration) = Timecode.FormatPair(TimeSpan.FromSeconds(30), TimeSpan.Zero);

        Assert.Equal("00:30", position);
        Assert.Equal("--:--", duration);
    }

    [Fact]
    public void FormatPair_matches_placeholder_width_to_a_long_live_position()
    {
        var (position, duration) = Timecode.FormatPair(TimeSpan.FromHours(2), TimeSpan.Zero);

        Assert.Equal("02:00:00", position);
        Assert.Equal("--:--:--", duration);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void FromSeconds_absorbs_the_non_finite_values_libmpv_reports(double raw) =>
        Assert.Equal(TimeSpan.Zero, Timecode.FromSeconds(raw));

    [Fact]
    public void FromSeconds_clamps_an_absurd_duration()
    {
        Assert.Equal(Timecode.MaximumDuration, Timecode.FromSeconds(1e12));
    }

    [Fact]
    public void Format_renders_a_negative_position_with_a_single_leading_sign()
    {
        Assert.Equal("-00:05", Timecode.Format(TimeSpan.FromSeconds(-5)));
    }

    // ── The pair keeps one shape ──
    //
    // FormatPair exists for exactly one reason: both halves must have the same
    // number of fields, so the transport bar does not change width as a film
    // crosses the one-hour mark. A case where one half grows an hours field and
    // the other does not is the failure this type was written to prevent.

    private static int FieldCount(string formatted) => formatted.Split(':').Length;

    [Theory]
    [InlineData(-7200, 0)]      // negative position, unknown duration
    [InlineData(-30, 0)]        // negative position, unknown duration, no hours
    [InlineData(7200, 0)]       // live stream, an hour in
    [InlineData(30, 0)]         // live stream, just started
    [InlineData(90, 5400)]      // ordinary film
    [InlineData(7200, 30)]      // position past a short duration
    public void Both_halves_of_a_pair_have_the_same_shape(double positionSeconds, double durationSeconds)
    {
        (string position, string duration) = Timecode.FormatPair(
            TimeSpan.FromSeconds(positionSeconds),
            TimeSpan.FromSeconds(durationSeconds));

        Assert.Equal(FieldCount(duration), FieldCount(position));
    }

    [Fact]
    public void An_unknown_duration_reads_as_a_placeholder_not_as_zero()
    {
        (string position, string duration) = Timecode.FormatPair(
            TimeSpan.FromSeconds(42), TimeSpan.Zero);

        // "00:00" would be a claim about the file. The dashes say the length is
        // not known, which for a live stream is the truth.
        Assert.Equal("00:42", position);
        Assert.Equal("--:--", duration);
    }

    [Fact]
    public void A_position_beyond_ninety_nine_hours_is_clamped_rather_than_wrapped()
    {
        Assert.Equal("99:00:00", Timecode.Format(TimeSpan.FromHours(500)));
        Assert.Equal(TimeSpan.FromHours(99).Negate(), Timecode.FromSeconds(-1e12));
    }

    [Fact]
    public void A_second_is_truncated_not_rounded()
    {
        // Rounding would show 01:00 for a file that is still at 59.999 seconds,
        // and the last second of every file would read as the next minute.
        Assert.Equal("00:59", Timecode.Format(TimeSpan.FromSeconds(59.999)));
    }
}
