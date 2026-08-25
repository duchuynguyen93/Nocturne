using Nocturne.Core.Playback;
using Xunit;

namespace Nocturne.Core.Tests.Playback;

public sealed class SeekMathTests
{
    private static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    [Fact]
    public void Stepping_back_near_the_start_lands_on_zero_not_a_negative_position()
    {
        TimeSpan result = SeekMath.Step(TimeSpan.FromSeconds(3), -SeekMath.DefaultStep, FiveMinutes);

        Assert.Equal(TimeSpan.Zero, result);
    }

    [Fact]
    public void Stepping_forward_near_the_end_stops_short_of_the_duration()
    {
        TimeSpan result = SeekMath.Step(
            FiveMinutes - TimeSpan.FromSeconds(2),
            SeekMath.DefaultStep,
            FiveMinutes);

        // Landing exactly on the duration makes the engine report end-of-file,
        // which advances the playlist instead of seeking.
        Assert.Equal(FiveMinutes - SeekMath.EndGuard, result);
        Assert.True(result < FiveMinutes);
    }

    [Fact]
    public void An_unknown_duration_leaves_forward_seeks_unclamped()
    {
        TimeSpan result = SeekMath.Step(TimeSpan.FromSeconds(30), SeekMath.DefaultStep, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(40), result);
    }

    [Fact]
    public void An_item_shorter_than_the_end_guard_only_seeks_to_zero()
    {
        TimeSpan result = SeekMath.ClampToRange(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(120));

        Assert.Equal(TimeSpan.Zero, result);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 150)]
    [InlineData(1.0, 299.75)]
    [InlineData(-4.0, 0)]
    [InlineData(9.0, 299.75)]
    public void A_track_fraction_maps_onto_the_playable_range(double fraction, double expectedSeconds)
    {
        TimeSpan? result = SeekMath.FromTrackFraction(fraction, FiveMinutes);

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result!.Value);
    }

    [Fact]
    public void A_track_fraction_is_meaningless_without_a_duration()
    {
        Assert.Null(SeekMath.FromTrackFraction(0.5, TimeSpan.Zero));
        Assert.Null(SeekMath.FromTrackFraction(double.NaN, FiveMinutes));
    }

    [Fact]
    public void Previous_restarts_the_item_once_far_enough_in()
    {
        TimeSpan threshold = TimeSpan.FromSeconds(3);

        Assert.False(SeekMath.PreviousShouldRestart(TimeSpan.FromSeconds(1), threshold));
        Assert.True(SeekMath.PreviousShouldRestart(TimeSpan.FromSeconds(30), threshold));
    }
}
