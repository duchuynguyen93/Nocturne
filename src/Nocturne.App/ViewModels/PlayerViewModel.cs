using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Nocturne.Core.Media;
using Nocturne.Core.Playback;
using Nocturne.Core.Playlist;
using Nocturne.Core.Text;
using Nocturne.Engine.Client;

namespace Nocturne.App.ViewModels;

/// <summary>
/// Presents playback state to the player window.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place that crosses from the engine's thread to the UI
/// thread. <see cref="PlayerEngine.SnapshotChanged"/> is raised on the engine's
/// event pump; everything below this class assumes the UI thread, so the
/// marshalling happens once, here, rather than being re-derived by each
/// consumer.
/// </para>
/// <para>
/// The seek bar is the awkward part. Binding it straight to the position makes
/// a drag fight the engine: the user moves the thumb, the engine reports the
/// old position a few milliseconds later, and the thumb snaps back.
/// <see cref="BeginScrub"/> and <see cref="EndScrub"/> suppress incoming
/// position updates for the duration of the gesture.
/// </para>
/// </remarks>
public sealed class PlayerViewModel : ObservableBase, IDisposable
{
    private readonly PlayerEngine _engine;
    private readonly DispatcherQueue _dispatcher;
    private readonly PlaylistModel _playlist = new();

    /// <summary>
    /// How far into an item "previous" stops meaning "the item before this one".
    /// </summary>
    private static readonly TimeSpan PreviousRestartThreshold = TimeSpan.FromSeconds(3);

    private string _windowTitle = "Nocturne";

    // Named TimecodeText, not Timecode: a property called Timecode would shadow
    // the Nocturne.Core.Text.Timecode helper class inside this type, and every
    // call to it would resolve against a string instead.
    private string _timecodeText = "00:00 / --:--";
    private string _overlayChipText = string.Empty;
    private double _progress;
    private double _volume = 100;
    private bool _isSeekable;
    private string _playPauseGlyph = "";
    private string _playPauseLabel = "Play";
    private string _volumeGlyph = "";
    private string? _errorMessage;
    private Visibility _emptyStateVisibility = Visibility.Visible;
    private Visibility _transportVisibility = Visibility.Collapsed;
    private Visibility _errorVisibility = Visibility.Collapsed;

    private bool _isScrubbing;
    private string? _renderFailure;
    private string? _transientError;
    private bool _disposed;

    /// <summary>Creates the view model over an engine.</summary>
    /// <param name="engine">An initialized engine. The view model does not own it.</param>
    /// <param name="dispatcher">The window's dispatcher queue.</param>
    public PlayerViewModel(PlayerEngine engine, DispatcherQueue dispatcher)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _engine = engine;
        _dispatcher = dispatcher;

        _engine.SnapshotChanged += OnSnapshotChanged;
        _engine.ReachedEnd += OnReachedEnd;
    }

    /// <summary>Window and title-bar caption.</summary>
    public string WindowTitle
    {
        get => _windowTitle;
        private set => Set(ref _windowTitle, value);
    }

    /// <summary>Elapsed and total time, as one already-formatted pair.</summary>
    public string TimecodeText
    {
        get => _timecodeText;
        private set => Set(ref _timecodeText, value);
    }

    /// <summary>Text of the overlay pill above the transport bar.</summary>
    public string OverlayChipText
    {
        get => _overlayChipText;
        private set => Set(ref _overlayChipText, value);
    }

    /// <summary>Playback position as a fraction of the duration, 0–1.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>Output volume, 0–100.</summary>
    public double Volume
    {
        get => _volume;
        private set => Set(ref _volume, value);
    }

    /// <summary>Whether the seek bar should accept input.</summary>
    public bool IsSeekable
    {
        get => _isSeekable;
        private set => Set(ref _isSeekable, value);
    }

    /// <summary>Segoe Fluent glyph for the play/pause button.</summary>
    public string PlayPauseGlyph
    {
        get => _playPauseGlyph;
        private set => Set(ref _playPauseGlyph, value);
    }

    /// <summary>Accessible name for the play/pause button.</summary>
    public string PlayPauseLabel
    {
        get => _playPauseLabel;
        private set => Set(ref _playPauseLabel, value);
    }

    /// <summary>Segoe Fluent glyph for the volume button.</summary>
    public string VolumeGlyph
    {
        get => _volumeGlyph;
        private set => Set(ref _volumeGlyph, value);
    }

    /// <summary>Why the current item failed, if it did.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => Set(ref _errorMessage, value);
    }

    /// <summary>Whether the "drop a file here" surface is shown.</summary>
    public Visibility EmptyStateVisibility
    {
        get => _emptyStateVisibility;
        set => Set(ref _emptyStateVisibility, value);
    }

    /// <summary>Whether the transport bar is shown.</summary>
    public Visibility TransportVisibility
    {
        get => _transportVisibility;
        private set => Set(ref _transportVisibility, value);
    }

    /// <summary>Whether the failure surface is shown.</summary>
    public Visibility ErrorVisibility
    {
        get => _errorVisibility;
        set => Set(ref _errorVisibility, value);
    }

    /// <summary>Opens a file, replacing the queue with its containing folder.</summary>
    /// <remarks>
    /// Loading the siblings is what makes Next and Previous mean anything after
    /// a double-click from Explorer, which is how most files reach the player.
    /// </remarks>
    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Whatever went wrong last time was about the previous request.
        _transientError = null;

        LoadSiblingQueue(path);
        _engine.Open(path);
    }

    /// <summary>Toggles between paused and playing.</summary>
    public void TogglePlayPause() => _engine.TogglePause();

    /// <summary>Moves to the next item, if the queue has one.</summary>
    public void Next()
    {
        if (_playlist.MoveNext(userInitiated: true) is { } next)
        {
            _engine.Open(next);
        }
    }

    /// <summary>
    /// Restarts the current item, or moves back if it only just started.
    /// </summary>
    public void Previous()
    {
        if (SeekMath.PreviousShouldRestart(_engine.Snapshot.Position, PreviousRestartThreshold))
        {
            _engine.SeekTo(TimeSpan.Zero);
            return;
        }

        if (_playlist.MovePrevious() is { } previous)
        {
            _engine.Open(previous);
        }
    }

    /// <summary>Steps the playhead by a relative amount.</summary>
    public void SeekBy(TimeSpan step) => _engine.SeekBy(step);

    /// <summary>Seeks to an absolute position.</summary>
    public void SeekTo(TimeSpan position) => _engine.SeekTo(position);

    /// <summary>Suppresses incoming position updates while the user drags.</summary>
    /// <remarks>
    /// Without this, a drag fights the engine: the user moves the thumb, the
    /// engine reports the old position a few milliseconds later, the binding
    /// writes it back, and the thumb snaps backwards under the pointer.
    /// </remarks>
    public void BeginScrub() => _isScrubbing = true;

    /// <summary>Commits a scrub gesture and resumes following the engine.</summary>
    /// <remarks>
    /// Guarded on the flag because the window commits from more than one event —
    /// a release and a capture loss both mean "the drag is over", and the slider
    /// raises both for an ordinary click. Without the guard the second one seeks
    /// again to the position the first one had already left behind.
    /// </remarks>
    public void EndScrub(double fraction)
    {
        if (!_isScrubbing)
        {
            return;
        }

        _isScrubbing = false;

        if (SeekMath.FromTrackFraction(fraction, _engine.Snapshot.Duration) is { } target)
        {
            _engine.SeekTo(target);
        }
    }

    /// <summary>Abandons a scrub gesture without seeking.</summary>
    /// <remarks>
    /// For a genuinely cancelled gesture only — a touch the system took away, a
    /// drag interrupted by something that is not the user letting go. Losing
    /// pointer capture is <em>not</em> that: a slider releases capture as part
    /// of an ordinary click, so treating capture loss as a cancellation put the
    /// old position back on every seek, and the thumb sprang backwards under the
    /// pointer each time.
    /// </remarks>
    public void CancelScrub()
    {
        if (!_isScrubbing)
        {
            return;
        }

        _isScrubbing = false;
        Progress = _engine.Snapshot.Progress;
    }

    /// <summary>
    /// Whether the user is currently working the volume slider.
    /// </summary>
    /// <remarks>
    /// The volume slider has the same feedback problem as the seek bar, for the
    /// same reason: libmpv echoes the previous value back a moment later. The
    /// window also uses this to tell a user-driven change from the binding
    /// writing a value in — <c>ValueChanged</c> fires for both and carries
    /// nothing that distinguishes them.
    /// </remarks>
    public bool IsAdjustingVolume { get; private set; }

    /// <summary>Starts a volume gesture.</summary>
    public void BeginVolumeAdjust() => IsAdjustingVolume = true;

    /// <summary>Ends a volume gesture.</summary>
    /// <remarks>
    /// It does not write the engine's value back. The engine echoes the new
    /// volume a few milliseconds later and the binding picks it up then; writing
    /// the pre-gesture value here first made the slider jump back to where it
    /// started and then forward again, on every adjustment.
    /// </remarks>
    public void EndVolumeAdjust() => IsAdjustingVolume = false;

    /// <summary>Sets output volume, 0–100.</summary>
    public void SetVolume(double volume) => _engine.SetVolume(volume);

    /// <summary>Toggles mute without changing the volume.</summary>
    public void ToggleMute() => _engine.SetMuted(!_engine.Snapshot.IsMuted);

    /// <summary>
    /// Records that the video pipeline could not be built, permanently.
    /// </summary>
    /// <remarks>
    /// Latched rather than shown once: nothing that happens later makes a
    /// missing GPU path work again, and ordinary playback snapshots would
    /// otherwise clear the message within milliseconds.
    /// </remarks>
    public void ReportRenderFailure(string message, string? logPath = null)
    {
        // The path goes in the message rather than into a separate control: the
        // person who needs it is reading this card, and telling them a log
        // exists without saying where is worse than not mentioning it.
        _renderFailure = logPath is null
            ? message
            : $"{message}\n\nFull details: {logPath}";
        ErrorMessage = _renderFailure;
        ErrorVisibility = Visibility.Visible;
        EmptyStateVisibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Shows a message about something the user just did, until the next file.
    /// </summary>
    /// <remarks>
    /// A dropped item that could not be read, a file picker that would not open:
    /// these are reported by the window itself, not by the engine, and writing
    /// straight to <see cref="ErrorMessage"/> put them at the mercy of the next
    /// snapshot — which arrives several times a second while anything is
    /// playing. The message appeared and vanished before it could be read.
    /// </remarks>
    public void ReportTransientError(string message)
    {
        _transientError = message;
        ErrorMessage = message;
        ErrorVisibility = Visibility.Visible;
        EmptyStateVisibility = Visibility.Collapsed;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.SnapshotChanged -= OnSnapshotChanged;
        _engine.ReachedEnd -= OnReachedEnd;
    }

    private void LoadSiblingQueue(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                _playlist.Load([path]);
                return;
            }

            List<string> siblings = [.. Directory
                .EnumerateFiles(directory)
                .Where(MediaFormats.IsPlayable)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)];

            int index = siblings.FindIndex(
                candidate => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                // The file is playing but its extension is not in MediaFormats —
                // an .iso, or something opened from the command line. Inserting
                // it keeps the queue's idea of "current" in step with what the
                // engine is actually playing; falling back to index 0 would make
                // Next jump from the wrong place.
                siblings.Insert(0, path);
                index = 0;
            }

            _playlist.Load(siblings, index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder — a disconnected share, a permission the user
            // does not have — must not stop the file they explicitly opened from
            // playing. It only costs them Next and Previous.
            _playlist.Load([path]);
        }
    }

    private void OnReachedEnd(object? sender, EventArgs e)
    {
        // The whole handler hops to the UI thread, not just the Open call. This
        // runs on the engine's pump thread, and the playlist is mutated from the
        // UI thread by Next, Previous, and Open. Advancing here would race a
        // user pressing Next as the file ends — which is exactly when people
        // press it — and could skip two items or tear a list mid-rebuild.
        _dispatcher.TryEnqueue(() =>
        {
            // The window can close between the enqueue above and this running:
            // a file ending as someone closes the window is not a rare
            // coincidence, it is when files end. Opening on a disposed engine
            // throws ObjectDisposedException on the UI thread during shutdown.
            if (_disposed)
            {
                return;
            }

            // Deliberately not user-initiated, so repeat-one replays rather than
            // advancing.
            if (_playlist.MoveNext() is { } next)
            {
                _engine.Open(next);
            }
        });
    }

    private void OnSnapshotChanged(object? sender, PlaybackSnapshot snapshot)
    {
        // Raised on the engine's pump thread. Everything below assumes the UI
        // thread, so the hop happens exactly here.
        _dispatcher.TryEnqueue(() => ApplySnapshot(snapshot));
    }

    private void ApplySnapshot(PlaybackSnapshot snapshot)
    {
        (string position, string duration) = Timecode.FormatPair(snapshot.Position, snapshot.Duration);
        TimecodeText = $"{position} / {duration}";

        if (!_isScrubbing)
        {
            Progress = snapshot.Progress;
        }

        IsSeekable = snapshot.IsSeekable;

        if (!IsAdjustingVolume)
        {
            Volume = snapshot.Volume;
        }

        bool playing = snapshot.Status == PlaybackStatus.Playing;
        PlayPauseGlyph = playing ? "" : "";
        PlayPauseLabel = playing ? "Pause" : "Play";

        // Three states, not two: a muted player and a player at zero volume look
        // the same on the slider but mean different things when you press it.
        VolumeGlyph = snapshot.IsMuted || snapshot.Volume <= 0.0
            ? ""
            : snapshot.Volume < 50.0 ? "" : "";

        string label = ResolveDisplayName(snapshot);
        WindowTitle = string.IsNullOrEmpty(label) ? "Nocturne" : $"{label} — Nocturne";
        OverlayChipText = string.IsNullOrEmpty(label) ? string.Empty : $"{label} · {position} / {duration}";

        // A render pipeline that failed to build stays on screen. It is a fault
        // of the app, not of the current file, and the engine keeps publishing
        // ordinary snapshots afterwards — letting those overwrite the message
        // would flash the one diagnostic the user needs and then hide it. On a
        // machine with no ANGLE, which today means every machine, that reads as
        // "the app opened and did nothing".
        // The transport bar stays. The card says audio still works, and hiding
        // the play button, the volume slider and the seek bar underneath that
        // sentence left the keyboard as the only way to act on it — with nothing
        // on screen saying so.
        TransportVisibility = snapshot.HasMedia ? Visibility.Visible : Visibility.Collapsed;

        if (_renderFailure is not null)
        {
            ErrorMessage = _renderFailure;
            ErrorVisibility = Visibility.Visible;
            EmptyStateVisibility = Visibility.Collapsed;
            return;
        }

        if (_transientError is not null)
        {
            ErrorMessage = _transientError;
            ErrorVisibility = Visibility.Visible;
            EmptyStateVisibility = Visibility.Collapsed;
            return;
        }

        ErrorMessage = snapshot.ErrorMessage;
        ErrorVisibility = snapshot.Status == PlaybackStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateVisibility = snapshot.Status == PlaybackStatus.Idle ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Picks the name to show for the current item.
    /// </summary>
    /// <remarks>
    /// Container metadata first, file name second. Plenty of files carry a
    /// title that is worse than their name — "Track 1", an encoder's default —
    /// but a title that exists was chosen by someone, and second-guessing that
    /// per file is not a rule this can encode.
    /// </remarks>
    private static string ResolveDisplayName(PlaybackSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Title))
        {
            return snapshot.Title!;
        }

        if (string.IsNullOrWhiteSpace(snapshot.Source))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFileName(snapshot.Source) ?? snapshot.Source;
        }
        catch (ArgumentException)
        {
            // A URL, or a path with characters Path rejects. Showing it whole is
            // better than showing nothing.
            return snapshot.Source;
        }
    }
}
