using Nocturne.Core.Playback;
using Nocturne.Core.Text;

namespace Nocturne.Engine.Client;

/// <summary>
/// Folds individual libmpv property changes into a whole
/// <see cref="PlaybackSnapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam that makes engine state testable. libmpv reports one
/// property at a time, on its own thread, in an order that is not the order the
/// UI would like: <c>duration</c> for the next file can arrive before
/// <c>path</c> has changed. Reducing each change into an immutable snapshot and
/// publishing the result whole means the UI can never render a frame that mixes
/// two files.
/// </para>
/// <para>
/// Every method is pure — snapshot in, snapshot out — so the whole state machine
/// is exercised in unit tests on any platform, without libmpv present.
/// </para>
/// </remarks>
public static class PlaybackSnapshotReducer
{
    /// <summary>Property names the engine observes.</summary>
    /// <remarks>
    /// The engine subscribes to exactly this list. Keeping the names in one
    /// place is what stops a reducer case from existing for a property nobody
    /// observes, which looks like working code and never runs.
    /// </remarks>
    public static class Properties
    {
        /// <summary>Playhead position in seconds.</summary>
        public const string TimePosition = "time-pos";

        /// <summary>Length of the current file in seconds.</summary>
        public const string Duration = "duration";

        /// <summary>Whether playback is paused.</summary>
        public const string Paused = "pause";

        /// <summary>Playback rate, where 1.0 is normal.</summary>
        public const string Speed = "speed";

        /// <summary>Output volume, 0–100.</summary>
        public const string Volume = "volume";

        /// <summary>Whether output is muted.</summary>
        public const string Muted = "mute";

        /// <summary>Path or URL of the current file.</summary>
        public const string Path = "path";

        /// <summary>Display title, from container metadata when present.</summary>
        public const string MediaTitle = "media-title";

        /// <summary>Whether the core is seeking or filling its buffer.</summary>
        public const string SeekingOrBuffering = "core-idle";

        /// <summary>Whether the demuxer is still filling its cache.</summary>
        public const string PausedForCache = "paused-for-cache";

        /// <summary>Every observed name, in subscription order.</summary>
        public static readonly IReadOnlyList<string> All =
        [
            TimePosition, Duration, Paused, Speed, Volume, Muted,
            Path, MediaTitle, SeekingOrBuffering, PausedForCache,
        ];
    }

    /// <summary>
    /// Applies one property change.
    /// </summary>
    /// <param name="current">Snapshot to fold the change into.</param>
    /// <param name="name">Property name from libmpv.</param>
    /// <param name="value">
    /// New value, or <see langword="null"/> when the property currently has none.
    /// </param>
    /// <returns>
    /// The updated snapshot, or <paramref name="current"/> unchanged when the
    /// property is not one the UI reflects.
    /// </returns>
    public static PlaybackSnapshot Apply(PlaybackSnapshot current, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(current);

        return name switch
        {
            Properties.TimePosition => value is double position
                ? current with { Position = Timecode.FromSeconds(position) }

                // A null time-pos means "between files". Holding the previous
                // position would leave the seek bar parked mid-track while the
                // next file opens.
                : current with { Position = TimeSpan.Zero },

            // A negative duration is meaningless, and a damaged container will
            // report one. Folding it to zero routes it into the "unknown length"
            // path that live streams already use; leaving it negative would make
            // SeekMath treat the file as unbounded and let a seek run past the
            // end, where libmpv reports end-of-file and the playlist advances.
            Properties.Duration => current with
            {
                Duration = value is double duration && duration > 0
                    ? Timecode.FromSeconds(duration)
                    : TimeSpan.Zero,
            },

            Properties.Paused => value is bool paused
                ? current with { Status = ResolveStatus(current.Status, paused) }
                : current,

            Properties.Speed => value is double speed && speed > 0
                ? current with { Speed = speed }
                : current,

            Properties.Volume => value is double volume
                ? current with { Volume = Math.Clamp(volume, 0.0, 100.0) }
                : current,

            Properties.Muted => value is bool muted
                ? current with { IsMuted = muted }
                : current,

            Properties.Path => current with { Source = value as string },

            // media-title falls back to the file name inside libmpv, so an empty
            // string here means the file has no title at all rather than that
            // the property has not arrived yet.
            Properties.MediaTitle => current with
            {
                Title = string.IsNullOrWhiteSpace(value as string) ? null : (string)value!,
            },

            Properties.PausedForCache => value is bool stalled
                ? current with { IsBuffering = stalled }
                : current,

            _ => current,
        };
    }

    /// <summary>
    /// Moves the snapshot to the state a new file starts in.
    /// </summary>
    /// <remarks>
    /// Clearing position, duration, and the previous error is what prevents the
    /// transport bar from showing the outgoing file's length for the fraction of
    /// a second before the new one reports its own.
    /// </remarks>
    public static PlaybackSnapshot BeginOpening(PlaybackSnapshot current, string source)
    {
        ArgumentNullException.ThrowIfNull(current);

        return current with
        {
            Status = PlaybackStatus.Opening,
            Source = source,
            Title = null,
            Position = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
            IsBuffering = false,
            ErrorMessage = null,
        };
    }

    /// <summary>Moves the snapshot to the state after a file's tracks are known.</summary>
    public static PlaybackSnapshot MarkLoaded(PlaybackSnapshot current, bool paused)
    {
        ArgumentNullException.ThrowIfNull(current);

        return current with
        {
            Status = paused ? PlaybackStatus.Paused : PlaybackStatus.Playing,
            ErrorMessage = null,
        };
    }

    /// <summary>Records that the current item failed to play.</summary>
    public static PlaybackSnapshot MarkFailed(PlaybackSnapshot current, string message)
    {
        ArgumentNullException.ThrowIfNull(current);

        return current with
        {
            Status = PlaybackStatus.Failed,
            ErrorMessage = message,
            IsBuffering = false,
        };
    }

    /// <summary>Records that the current item played to its end.</summary>
    public static PlaybackSnapshot MarkEnded(PlaybackSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return current with
        {
            Status = PlaybackStatus.Ended,
            IsBuffering = false,

            // Pin the playhead to the end so the seek bar reads full rather than
            // snapping back to zero when time-pos clears.
            Position = current.Duration > TimeSpan.Zero ? current.Duration : current.Position,
        };
    }

    /// <summary>Returns the snapshot for a player with nothing loaded.</summary>
    public static PlaybackSnapshot Reset(PlaybackSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        // Volume, mute, and speed are player settings, not file state; a user who
        // set 60% volume expects it to survive opening the next file.
        return PlaybackSnapshot.Idle with
        {
            Volume = current.Volume,
            IsMuted = current.IsMuted,
            Speed = current.Speed,
        };
    }

    private static PlaybackStatus ResolveStatus(PlaybackStatus current, bool paused)
    {
        // A pause change while opening or after a failure says nothing about
        // whether a file is playable, so it must not promote the status.
        if (current is PlaybackStatus.Idle or PlaybackStatus.Opening or PlaybackStatus.Failed)
        {
            return current;
        }

        return paused ? PlaybackStatus.Paused : PlaybackStatus.Playing;
    }
}
