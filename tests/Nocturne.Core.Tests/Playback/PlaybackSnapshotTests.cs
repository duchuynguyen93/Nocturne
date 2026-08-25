using Nocturne.Core.Playback;
using Xunit;

namespace Nocturne.Core.Tests.Playback;

public sealed class PlaybackSnapshotTests
{
    [Fact]
    public void An_idle_player_exposes_no_transport()
    {
        Assert.False(PlaybackSnapshot.Idle.HasMedia);
        Assert.False(PlaybackSnapshot.Idle.IsSeekable);
        Assert.Equal(0.0, PlaybackSnapshot.Idle.Progress);
    }

    [Fact]
    public void Progress_of_an_unknown_duration_is_zero_rather_than_NaN()
    {
        var snapshot = new PlaybackSnapshot
        {
            Status = PlaybackStatus.Playing,
            Position = TimeSpan.FromSeconds(30),
            Duration = TimeSpan.Zero,
        };

        // A NaN width silently breaks the whole layout pass rather than the one
        // rectangle that produced it.
        Assert.Equal(0.0, snapshot.Progress);
        Assert.False(double.IsNaN(snapshot.Progress));
        Assert.True(snapshot.HasMedia);
        Assert.False(snapshot.IsSeekable);
    }

    [Fact]
    public void Progress_is_clamped_when_the_engine_overshoots_the_duration()
    {
        var snapshot = new PlaybackSnapshot
        {
            Status = PlaybackStatus.Playing,
            Position = TimeSpan.FromSeconds(305),
            Duration = TimeSpan.FromSeconds(300),
        };

        Assert.Equal(1.0, snapshot.Progress);
    }

    [Fact]
    public void A_failed_item_offers_no_transport()
    {
        var snapshot = new PlaybackSnapshot
        {
            Status = PlaybackStatus.Failed,
            ErrorMessage = "Unrecognized file format",
            Duration = TimeSpan.FromMinutes(4),
        };

        Assert.False(snapshot.HasMedia);
        Assert.False(snapshot.IsSeekable);
    }

    [Fact]
    public void An_ended_item_stays_seekable_so_the_user_can_replay_it()
    {
        var snapshot = new PlaybackSnapshot
        {
            Status = PlaybackStatus.Ended,
            Position = TimeSpan.FromMinutes(4),
            Duration = TimeSpan.FromMinutes(4),
        };

        Assert.True(snapshot.IsSeekable);
    }
}
