namespace Nocturne.Core.Playback;

/// <summary>
/// What the player is doing right now, as a value the UI can bind to.
/// </summary>
/// <remarks>
/// The engine reports properties one at a time and out of order: a
/// <c>duration</c> change can arrive before the <c>path</c> that produced it.
/// Binding the UI directly to those events produces frames where the file name
/// belongs to the previous item and the duration to the next one. Instead the
/// engine layer folds every property change into one immutable snapshot and
/// publishes it whole, so the transport bar can never show a mixed state.
/// </remarks>
public sealed record PlaybackSnapshot
{
    /// <summary>A player with no file loaded.</summary>
    public static readonly PlaybackSnapshot Idle = new();

    /// <summary>Current lifecycle stage.</summary>
    public PlaybackStatus Status { get; init; } = PlaybackStatus.Idle;

    /// <summary>Absolute path or URL of the current item, if any.</summary>
    public string? Source { get; init; }

    /// <summary>Display title: container metadata if present, else the file name.</summary>
    public string? Title { get; init; }

    /// <summary>Position of the playhead.</summary>
    public TimeSpan Position { get; init; }

    /// <summary>
    /// Total length, or <see cref="TimeSpan.Zero"/> when the engine does not
    /// know it — live streams and some malformed containers never report one.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Playback rate, where 1.0 is normal speed.</summary>
    public double Speed { get; init; } = 1.0;

    /// <summary>Output volume in the range 0–100.</summary>
    public double Volume { get; init; } = 100.0;

    /// <summary>Whether audio output is muted, independently of <see cref="Volume"/>.</summary>
    public bool IsMuted { get; init; }

    /// <summary>Whether the engine is filling its buffer rather than presenting frames.</summary>
    public bool IsBuffering { get; init; }

    /// <summary>Last fatal error for the current item, if it failed to play.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Whether the transport controls should be interactive.</summary>
    public bool HasMedia => Status is PlaybackStatus.Playing or PlaybackStatus.Paused or PlaybackStatus.Ended;

    /// <summary>
    /// Whether the seek bar can be dragged. A file whose duration is unknown is
    /// playing, but there is nothing meaningful to scrub across.
    /// </summary>
    public bool IsSeekable => HasMedia && Duration > TimeSpan.Zero;

    /// <summary>
    /// Fraction of the file that has played, in the range 0–1.
    /// </summary>
    /// <remarks>
    /// Returns 0 rather than dividing when the duration is unknown, which is the
    /// difference between an empty progress track and a NaN-width rectangle that
    /// silently breaks the whole layout pass.
    /// </remarks>
    public double Progress
    {
        get
        {
            if (Duration <= TimeSpan.Zero)
            {
                return 0.0;
            }

            double ratio = Position.TotalSeconds / Duration.TotalSeconds;
            return ratio switch
            {
                < 0.0 => 0.0,
                > 1.0 => 1.0,
                _ => ratio,
            };
        }
    }
}
