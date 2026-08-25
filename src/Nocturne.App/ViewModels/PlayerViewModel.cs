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

    /// <summary>Suppresses incoming position updates while the user drags.</summary>
    public void BeginScrub() => _isScrubbing = true;

    /// <summary>Commits a scrub gesture and resumes following the engine.</summary>
    public void EndScrub(double fraction)
    {
        _isScrubbing = false;

        if (SeekMath.FromTrackFraction(fraction, _engine.Snapshot.Duration) is { } target)
        {
            _engine.SeekTo(target);
        }
    }

    /// <summary>Sets output volume, 0–100.</summary>
    public void SetVolume(double volume) => _engine.SetVolume(volume);

    /// <summary>Toggles mute without changing the volume.</summary>
    public void ToggleMute() => _engine.SetMuted(!_engine.Snapshot.IsMuted);

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

            _playlist.Load(siblings.Count == 0 ? [path] : siblings, Math.Max(index, 0));
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
        // Deliberately not user-initiated, so repeat-one replays rather than
        // advancing.
        if (_playlist.MoveNext() is { } next)
        {
            _dispatcher.TryEnqueue(() => _engine.Open(next));
        }
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
        Volume = snapshot.Volume;

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

        ErrorMessage = snapshot.ErrorMessage;
        ErrorVisibility = snapshot.Status == PlaybackStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateVisibility = snapshot.Status == PlaybackStatus.Idle ? Visibility.Visible : Visibility.Collapsed;
        TransportVisibility = snapshot.HasMedia ? Visibility.Visible : Visibility.Collapsed;
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
