using System.Globalization;

namespace Nocturne.App.Services;

/// <summary>
/// Remembers whether the last attempt to build the video pipeline survived.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline crosses into three native libraries — Direct3D, ANGLE and
/// libmpv — and a fault in any of them is not an exception. It is an access
/// violation or an <c>abort()</c>, which ends the process outright: no catch
/// block runs, no dialog appears, and from the user's side the app simply does
/// not open. Worse, it happens during the first layout pass, so every subsequent
/// launch does the same thing. An app that cannot be opened cannot even be used
/// to play audio, and cannot be used to read its own log.
/// </para>
/// <para>
/// So the attempt is bracketed by a marker file. It is written before the first
/// native call and deleted once the pipeline stands. Finding it at startup means
/// the previous run died in there, and the video path is skipped for this run —
/// the app opens, plays sound, and says why the picture is missing.
/// </para>
/// <para>
/// Deliberately a file rather than a setting. It has to survive a process that
/// is about to be killed without warning, which rules out anything flushed at
/// exit.
/// </para>
/// </remarks>
public static class RenderGuard
{
    private const string MarkerName = "render-attempt.marker";

    private static string? _markerPath;

    /// <summary>
    /// Whether a previous run died while building the pipeline.
    /// </summary>
    /// <remarks>
    /// Read once at startup, before the marker for this run is written, so it
    /// keeps answering for the whole session.
    /// </remarks>
    public static bool PreviousAttemptFailed { get; private set; }

    /// <summary>Where the marker lives, for a message that tells the user how to reset.</summary>
    public static string? MarkerPath => _markerPath;

    /// <summary>Reads the outcome of the previous run. Call once, at startup.</summary>
    public static void Initialize(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            _markerPath = Path.Combine(directory, MarkerName);
            PreviousAttemptFailed = File.Exists(_markerPath);
        }
#pragma warning disable CA1031 // A guard that throws is worse than no guard.
        catch (Exception)
#pragma warning restore CA1031
        {
            // No marker means no protection, which is exactly where this started.
            _markerPath = null;
            PreviousAttemptFailed = false;
        }
    }

    /// <summary>Records that an attempt is under way.</summary>
    public static void BeginAttempt() => WriteMarker(exists: true);

    /// <summary>Records that the attempt completed without killing the process.</summary>
    /// <remarks>
    /// Called on the failure path too. A <see cref="RenderGuard"/> is about
    /// crashes, not about errors: an exception means the code reached its own
    /// handler, which is precisely the case that needs no protection.
    /// </remarks>
    public static void EndAttempt() => WriteMarker(exists: false);

    /// <summary>Raised when the marker could not be written or removed.</summary>
    /// <remarks>
    /// A failed delete is not harmless: the next launch reads the leftover
    /// marker as a crash and turns the video path off for a run that was
    /// perfectly healthy. Swallowing that silently leaves no way to tell the
    /// two apart, so it is reported for the log rather than kept quiet.
    /// </remarks>
    public static event Action<string>? Failed;

    private static void WriteMarker(bool exists)
    {
        if (_markerPath is null)
        {
            return;
        }

        try
        {
            if (exists)
            {
                File.WriteAllText(_markerPath, DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
            }
            else if (File.Exists(_markerPath))
            {
                File.Delete(_markerPath);
            }
        }
#pragma warning disable CA1031 // A guard that throws is worse than no guard.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Failed?.Invoke(
                $"{(exists ? "writing" : "removing")} {_markerPath} failed: {error.Message}");
        }
    }
}
