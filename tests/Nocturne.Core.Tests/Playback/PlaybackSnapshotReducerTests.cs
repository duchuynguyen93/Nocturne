using Nocturne.Core.Playback;
using Nocturne.Engine.Client;
using Xunit;

using Props = Nocturne.Engine.Client.PlaybackSnapshotReducer.Properties;

namespace Nocturne.Core.Tests.Playback;

public sealed class PlaybackSnapshotReducerTests
{
    private static PlaybackSnapshot Playing(TimeSpan position, TimeSpan duration) => new()
    {
        Status = PlaybackStatus.Playing,
        Source = "a.mkv",
        Position = position,
        Duration = duration,
    };

    [Fact]
    public void Opening_a_file_clears_the_previous_files_duration()
    {
        PlaybackSnapshot previous = Playing(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(94));

        PlaybackSnapshot opening = PlaybackSnapshotReducer.BeginOpening(previous, "b.mkv");

        // Otherwise the transport bar shows a 94-minute film's length under the
        // name of the four-minute clip that replaced it.
        Assert.Equal(TimeSpan.Zero, opening.Duration);
        Assert.Equal(TimeSpan.Zero, opening.Position);
        Assert.Equal("b.mkv", opening.Source);
        Assert.Null(opening.Title);
        Assert.Equal(PlaybackStatus.Opening, opening.Status);
    }

    [Fact]
    public void Opening_a_file_clears_the_previous_files_error()
    {
        PlaybackSnapshot failed = PlaybackSnapshotReducer.MarkFailed(
            PlaybackSnapshot.Idle, "Unrecognized file format");

        PlaybackSnapshot opening = PlaybackSnapshotReducer.BeginOpening(failed, "b.mkv");

        Assert.Null(opening.ErrorMessage);
    }

    [Fact]
    public void A_null_time_position_rewinds_rather_than_holding_the_old_value()
    {
        PlaybackSnapshot current = Playing(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4));

        PlaybackSnapshot next = PlaybackSnapshotReducer.Apply(current, Props.TimePosition, value: null);

        // libmpv clears time-pos between files. Holding the previous value parks
        // the seek bar mid-track while the next file opens.
        Assert.Equal(TimeSpan.Zero, next.Position);
    }

    [Fact]
    public void A_pause_change_while_opening_does_not_promote_the_status()
    {
        PlaybackSnapshot opening = PlaybackSnapshotReducer.BeginOpening(PlaybackSnapshot.Idle, "a.mkv");

        PlaybackSnapshot next = PlaybackSnapshotReducer.Apply(opening, Props.Paused, value: false);

        Assert.Equal(PlaybackStatus.Opening, next.Status);
    }

    [Fact]
    public void A_pause_change_after_a_failure_does_not_resurrect_the_item()
    {
        PlaybackSnapshot failed = PlaybackSnapshotReducer.MarkFailed(PlaybackSnapshot.Idle, "broken");

        PlaybackSnapshot next = PlaybackSnapshotReducer.Apply(failed, Props.Paused, value: false);

        Assert.Equal(PlaybackStatus.Failed, next.Status);
    }

    [Fact]
    public void A_pause_change_during_playback_toggles_the_status()
    {
        PlaybackSnapshot current = Playing(TimeSpan.Zero, TimeSpan.FromMinutes(4));

        Assert.Equal(
            PlaybackStatus.Paused,
            PlaybackSnapshotReducer.Apply(current, Props.Paused, value: true).Status);

        Assert.Equal(
            PlaybackStatus.Playing,
            PlaybackSnapshotReducer.Apply(current with { Status = PlaybackStatus.Paused }, Props.Paused, false)
                .Status);
    }

    [Fact]
    public void An_empty_media_title_is_treated_as_absent()
    {
        PlaybackSnapshot current = Playing(TimeSpan.Zero, TimeSpan.FromMinutes(4)) with { Title = "old" };

        Assert.Null(PlaybackSnapshotReducer.Apply(current, Props.MediaTitle, "   ").Title);
        Assert.Equal("New", PlaybackSnapshotReducer.Apply(current, Props.MediaTitle, "New").Title);
    }

    [Fact]
    public void Volume_is_clamped_to_the_range_the_slider_can_show()
    {
        PlaybackSnapshot current = PlaybackSnapshot.Idle;

        Assert.Equal(100.0, PlaybackSnapshotReducer.Apply(current, Props.Volume, 130.0).Volume);
        Assert.Equal(0.0, PlaybackSnapshotReducer.Apply(current, Props.Volume, -5.0).Volume);
    }

    [Fact]
    public void A_zero_speed_is_rejected_rather_than_stored()
    {
        PlaybackSnapshot current = PlaybackSnapshot.Idle;

        // Dividing by a stored zero later is a harder bug to find than ignoring
        // a value libmpv should never send.
        Assert.Equal(1.0, PlaybackSnapshotReducer.Apply(current, Props.Speed, 0.0).Speed);
        Assert.Equal(1.5, PlaybackSnapshotReducer.Apply(current, Props.Speed, 1.5).Speed);
    }

    [Fact]
    public void Reaching_the_end_pins_the_playhead_to_the_duration()
    {
        PlaybackSnapshot current = Playing(TimeSpan.FromMinutes(3.9), TimeSpan.FromMinutes(4));

        PlaybackSnapshot ended = PlaybackSnapshotReducer.MarkEnded(current);

        Assert.Equal(PlaybackStatus.Ended, ended.Status);
        Assert.Equal(TimeSpan.FromMinutes(4), ended.Position);
        Assert.Equal(1.0, ended.Progress);
    }

    [Fact]
    public void Resetting_keeps_player_settings_and_drops_file_state()
    {
        PlaybackSnapshot current = Playing(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4)) with
        {
            Volume = 60.0,
            IsMuted = true,
            Speed = 1.5,
        };

        PlaybackSnapshot reset = PlaybackSnapshotReducer.Reset(current);

        Assert.Equal(60.0, reset.Volume);
        Assert.True(reset.IsMuted);
        Assert.Equal(1.5, reset.Speed);
        Assert.Null(reset.Source);
        Assert.Equal(PlaybackStatus.Idle, reset.Status);
    }

    [Fact]
    public void An_unobserved_property_leaves_the_snapshot_untouched()
    {
        PlaybackSnapshot current = Playing(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4));

        Assert.Same(current, PlaybackSnapshotReducer.Apply(current, "vo-configured", true));
    }

    [Fact]
    public void Every_observed_property_has_a_reducer_case()
    {
        PlaybackSnapshot current = Playing(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4));

        // core-idle is observed for the render layer's benefit and deliberately
        // does not alter the snapshot, so it is the one exception.
        string[] expectedToReduce = [.. Props.All.Where(p => p != Props.SeekingOrBuffering)];

        foreach (string property in expectedToReduce)
        {
            object? probe = property switch
            {
                Props.Paused or Props.Muted or Props.PausedForCache => true,
                Props.Path or Props.MediaTitle => "probe",
                _ => 2.0,
            };

            Assert.NotSame(current, PlaybackSnapshotReducer.Apply(current, property, probe));
        }
    }
}
