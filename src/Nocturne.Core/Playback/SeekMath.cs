namespace Nocturne.Core.Playback;

/// <summary>
/// Turns user seek gestures into absolute positions the engine can accept.
/// </summary>
/// <remarks>
/// Every seek path in the app funnels through here rather than computing
/// <c>position + 10s</c> at the call site, because all of them share the same
/// three failure modes: a negative target near the start of a file, a target
/// past the end that makes the engine report end-of-file instead of seeking,
/// and a division by an unknown duration.
/// </remarks>
public static class SeekMath
{
    /// <summary>
    /// How far short of the end an absolute seek is allowed to land.
    /// </summary>
    /// <remarks>
    /// Seeking to exactly <c>duration</c> lands past the last decodable frame,
    /// so the engine raises end-of-file and the playlist advances — which reads
    /// to the user as "dragging to the end skipped my file". Stopping just short
    /// leaves the playhead on a real frame.
    /// </remarks>
    public static readonly TimeSpan EndGuard = TimeSpan.FromMilliseconds(250);

    /// <summary>Step used by the arrow keys and the J/L shortcuts.</summary>
    public static readonly TimeSpan DefaultStep = TimeSpan.FromSeconds(10);

    /// <summary>Step used by Shift+arrow for fine positioning.</summary>
    public static readonly TimeSpan FineStep = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Clamps an absolute target into the playable range of the current item.
    /// </summary>
    /// <param name="target">Requested position.</param>
    /// <param name="duration">
    /// Length of the item, or <see cref="TimeSpan.Zero"/> when unknown.
    /// </param>
    public static TimeSpan ClampToRange(TimeSpan target, TimeSpan duration)
    {
        if (target < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (duration <= TimeSpan.Zero)
        {
            // Unknown duration: there is no upper bound to clamp against, and
            // inventing one would make live streams unseekable.
            return target;
        }

        TimeSpan upperBound = duration - EndGuard;
        if (upperBound < TimeSpan.Zero)
        {
            // Item shorter than the guard itself. Anything but the start would
            // be past the end.
            return TimeSpan.Zero;
        }

        return target > upperBound ? upperBound : target;
    }

    /// <summary>
    /// Applies a relative step to the current position.
    /// </summary>
    /// <remarks>
    /// Stepping back from 3 seconds by 10 must land on 0, not on -7. libmpv
    /// accepts a negative <c>seek</c> target and clamps it internally, but it
    /// reports the pre-clamp value back through the <c>time-pos</c> property
    /// first, which makes the transport bar flash a negative timecode.
    /// </remarks>
    public static TimeSpan Step(TimeSpan position, TimeSpan step, TimeSpan duration) =>
        ClampToRange(position + step, duration);

    /// <summary>
    /// Converts a scrub gesture on the progress track into a position.
    /// </summary>
    /// <param name="fraction">
    /// Where along the track the pointer landed, in the range 0–1. Values
    /// outside that range are clamped rather than rejected, because a drag that
    /// leaves the control horizontally should pin to the nearest end instead of
    /// cancelling.
    /// </param>
    /// <param name="duration">Length of the item.</param>
    /// <returns>
    /// The absolute target, or <see langword="null"/> when the item has no known
    /// duration and therefore cannot be scrubbed.
    /// </returns>
    public static TimeSpan? FromTrackFraction(double fraction, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || double.IsNaN(fraction))
        {
            return null;
        }

        double clamped = fraction switch
        {
            < 0.0 => 0.0,
            > 1.0 => 1.0,
            _ => fraction,
        };

        return ClampToRange(TimeSpan.FromSeconds(duration.TotalSeconds * clamped), duration);
    }

    /// <summary>
    /// Decides whether pressing "previous" should restart the current item or
    /// move to the one before it.
    /// </summary>
    /// <remarks>
    /// This is the behaviour every music and video player shares and nobody
    /// documents: once you are far enough into an item, "previous" means "start
    /// this again". Only near the beginning does it move backwards through the
    /// playlist.
    /// </remarks>
    public static bool PreviousShouldRestart(TimeSpan position, TimeSpan restartThreshold) =>
        position >= restartThreshold;
}
