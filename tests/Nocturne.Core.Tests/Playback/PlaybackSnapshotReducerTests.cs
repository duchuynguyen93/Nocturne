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

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-3600.0)]
    public void A_negative_duration_is_folded_into_the_unknown_length_path(double reported)
    {
        // A damaged container reports one of these. Left negative, SeekMath
        // would treat the file as unbounded and let a seek run past the end,
        // where libmpv raises end-of-file and the playlist advances.
        PlaybackSnapshot current = Playing(TimeSpan.Zero, TimeSpan.FromMinutes(4));

        PlaybackSnapshot next = PlaybackSnapshotReducer.Apply(current, Props.Duration, reported);

        Assert.Equal(TimeSpan.Zero, next.Duration);
        Assert.False(next.IsSeekable);
        Assert.Equal(0.0, next.Progress);
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

        // No exceptions. This test used to exclude core-idle, with a comment
        // saying it was observed "for the render layer's benefit" and
        // deliberately changed nothing — the render layer never read it, and
        // every one of its changes was marshalled across the event thread and
        // dropped. An exception list is how a coverage test comes to certify the
        // gap it was written to find, so there is no longer a list: a property
        // that nothing reduces does not belong in the subscription.
        foreach (string property in Props.All)
        {
            object? probe = property switch
            {
                Props.Paused or Props.Muted or Props.PausedForCache
                    or Props.EofReached => true,
                Props.Path or Props.MediaTitle => "probe",
                _ => 2.0,
            };

            Assert.NotSame(current, PlaybackSnapshotReducer.Apply(current, property, probe));
        }
    }

    // ── Values libmpv is allowed to publish and the app is not allowed to hold ──
    //
    // Every one of these arrives as a plain double from a real property, so the
    // type test in the reducer lets it through. What is being checked is that a
    // value which is a valid double but not a valid *volume* or *speed* leaves
    // the snapshot alone, rather than being stored and bound to a control.

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_non_finite_volume_leaves_the_previous_volume_alone(double reported)
    {
        PlaybackSnapshot current = Playing(TimeSpan.Zero, TimeSpan.FromMinutes(1)) with { Volume = 70 };

        PlaybackSnapshot next = PlaybackSnapshotReducer.Apply(current, Props.Volume, reported);

        // Math.Clamp(NaN, 0, 100) is NaN, not 0 and not 100. Stored, it reaches
        // the volume slider, whose Value cannot represent it — the control goes
        // blank and every later drag reads back NaN.
        Assert.Equal(70, next.Volume);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void A_non_finite_speed_leaves_the_previous_speed_alone(double reported)
    {
        PlaybackSnapshot current = Playing(TimeSpan.Zero, TimeSpan.FromMinutes(1)) with { Speed = 1.5 };

        PlaybackSnapshot next = PlaybackSnapshotReducer.Apply(current, Props.Speed, reported);

        Assert.Equal(1.5, next.Speed);
    }

    [Fact]
    public void Reaching_the_end_survives_the_pause_that_follows_it()
    {
        PlaybackSnapshot ended = PlaybackSnapshotReducer.MarkEnded(
            Playing(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3)));

        Assert.Equal(PlaybackStatus.Ended, ended.Status);

        // keep-open-pause=yes means libmpv pauses when it reaches the end, so
        // this property change always arrives just after. Letting it decide the
        // status turns "finished, press play to watch again" into an ordinary
        // pause in the middle of a file, and the transport bar stops offering
        // to replay.
        PlaybackSnapshot afterPause = PlaybackSnapshotReducer.Apply(ended, Props.Paused, true);

        Assert.Equal(PlaybackStatus.Ended, afterPause.Status);
    }

    [Fact]
    public void Starting_playback_again_after_the_end_leaves_the_ended_state()
    {
        PlaybackSnapshot ended = PlaybackSnapshotReducer.MarkEnded(
            Playing(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3)));

        // The other half of the rule above: unpausing is a real decision by the
        // user, and it must not be ignored just because the file had finished.
        PlaybackSnapshot resumed = PlaybackSnapshotReducer.Apply(ended, Props.Paused, false);

        Assert.Equal(PlaybackStatus.Playing, resumed.Status);
    }

    // ── Reaching the end of a file ──

    [Fact]
    public void The_eof_property_is_observed_because_end_of_file_events_never_arrive()
    {
        // keep-open=yes stops libmpv unloading the file at the end — that is
        // what holds the last frame on screen — and MPV_EVENT_END_FILE only
        // arrives after a file is unloaded. So this property is the only signal
        // that playback finished. Dropping it from the observed list makes the
        // playlist stop advancing, silently.
        Assert.Contains(Props.EofReached, PlaybackSnapshotReducer.Properties.All);
    }

    [Fact]
    public void Running_past_the_end_ends_playback()
    {
        PlaybackSnapshot playing = Playing(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3));

        PlaybackSnapshot ended = PlaybackSnapshotReducer.Apply(playing, Props.EofReached, true);

        Assert.Equal(PlaybackStatus.Ended, ended.Status);
        Assert.Equal(TimeSpan.FromMinutes(3), ended.Position);
    }

    [Fact]
    public void The_eof_flag_clearing_does_not_undo_the_ended_state()
    {
        PlaybackSnapshot ended = PlaybackSnapshotReducer.Apply(
            Playing(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3)), Props.EofReached, true);

        // libmpv clears the flag as part of loading whatever comes next. Acting
        // on that edge would cancel the very state the app is about to react to.
        PlaybackSnapshot after = PlaybackSnapshotReducer.Apply(ended, Props.EofReached, false);

        Assert.Equal(PlaybackStatus.Ended, after.Status);
    }

    [Fact]
    public void Running_past_the_end_with_nothing_loaded_changes_nothing()
    {
        PlaybackSnapshot after = PlaybackSnapshotReducer.Apply(
            PlaybackSnapshot.Idle, Props.EofReached, true);

        Assert.Equal(PlaybackStatus.Idle, after.Status);
    }

    [Fact]
    public void A_negative_position_reads_as_the_start_not_as_a_negative_timecode()
    {
        PlaybackSnapshot playing = Playing(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));

        // libmpv echoes the pre-clamp target while a seek is in flight.
        PlaybackSnapshot after = PlaybackSnapshotReducer.Apply(playing, Props.TimePosition, -1.5);

        Assert.Equal(TimeSpan.Zero, after.Position);
    }

    [Fact]
    public void An_unobserved_property_is_not_left_in_the_subscription_list()
    {
        // Subscribing to a property with no case in Apply marshals a value
        // across the event thread on every change and throws it away.
        Assert.DoesNotContain(Props.SeekingOrBuffering, PlaybackSnapshotReducer.Properties.All);
    }
}
