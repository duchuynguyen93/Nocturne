using Nocturne.Core.Playback;
using Nocturne.Engine.Interop;

namespace Nocturne.Engine.Client;

/// <summary>
/// The playback façade the rest of the app talks to.
/// </summary>
/// <remarks>
/// <para>
/// Owns one <see cref="MpvClient"/>, subscribes to the properties the UI
/// reflects, folds every change through <see cref="PlaybackSnapshotReducer"/>,
/// and publishes one <see cref="SnapshotChanged"/> event carrying a complete
/// state. Nothing above this class sees a libmpv property name or a raw handle.
/// </para>
/// <para>
/// <see cref="SnapshotChanged"/> is raised on the engine's event thread. The
/// view model marshals to the UI thread in one place rather than every consumer
/// re-deriving that rule.
/// </para>
/// </remarks>
public sealed class PlayerEngine : IDisposable
{
    private readonly MpvClient _client;
    // A plain object rather than System.Threading.Lock: Core and Engine target
    // net8.0 to match the runtime the app ships against, and Lock is net9.0+.
    private readonly object _snapshotLock = new();

    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Idle;
    private bool _disposed;

    /// <summary>Creates and initializes the engine.</summary>
    /// <param name="options">Quality and behaviour options, applied at initialization.</param>
    public PlayerEngine(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = new MpvClient(options.ToMpvOptions());
        _client.PropertyChanged += OnPropertyChanged;
        _client.FileLoaded += OnFileLoaded;
        _client.EndFile += OnEndFile;
        _client.LogMessage += OnLogMessage;
        _client.Shutdown += OnShutdown;

        _client.RequestLogMessages(options.LogLevel);

        foreach (string property in PlaybackSnapshotReducer.Properties.All)
        {
            _client.ObserveProperty(property, FormatFor(property));
        }
    }

    /// <summary>Raised whenever any part of the playback state changes.</summary>
    /// <remarks>Raised on the engine's event thread, not the UI thread.</remarks>
    public event EventHandler<PlaybackSnapshot>? SnapshotChanged;

    /// <summary>Raised when the current item plays to its end.</summary>
    /// <remarks>
    /// Only end-of-file raises this. A stop the app itself requested does not,
    /// which is what stops "close this file" from starting the next one.
    /// </remarks>
    public event EventHandler? ReachedEnd;

    /// <summary>Raised for libmpv log lines at or above the configured level.</summary>
    public event EventHandler<MpvLogEventArgs>? LogMessage;

    /// <summary>The current playback state.</summary>
    public PlaybackSnapshot Snapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>The underlying client, for the render layer's context creation.</summary>
    internal MpvClient Client => _client;

    /// <summary>The native handle the render context attaches to.</summary>
    public nint NativeHandle => _client.Handle;

    /// <summary>Loads a file or URL, replacing whatever is playing.</summary>
    public void Open(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        Mutate(current => PlaybackSnapshotReducer.BeginOpening(current, source));
        _client.Command("loadfile", source, "replace");
    }

    /// <summary>Stops playback and unloads the current item.</summary>
    public void Stop()
    {
        _client.Command("stop");
        Mutate(PlaybackSnapshotReducer.Reset);
    }

    /// <summary>Pauses or resumes.</summary>
    public void SetPaused(bool paused) => _client.SetProperty("pause", paused);

    /// <summary>Toggles between paused and playing.</summary>
    public void TogglePause() => SetPaused(Snapshot.Status != PlaybackStatus.Paused);

    /// <summary>
    /// Seeks to an absolute position.
    /// </summary>
    /// <remarks>
    /// Clamped through <see cref="SeekMath"/> first. Handing libmpv a target at
    /// or past the duration makes it raise end-of-file instead of seeking, which
    /// reads to the user as the file being skipped.
    /// </remarks>
    public void SeekTo(TimeSpan position)
    {
        TimeSpan target = SeekMath.ClampToRange(position, Snapshot.Duration);
        _client.Command(
            "seek",
            target.TotalSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            "absolute+exact");
    }

    /// <summary>Seeks by a relative amount, clamped to the playable range.</summary>
    public void SeekBy(TimeSpan step)
    {
        PlaybackSnapshot current = Snapshot;
        SeekTo(SeekMath.Step(current.Position, step, current.Duration));
    }

    /// <summary>Sets output volume, 0–100.</summary>
    public void SetVolume(double volume) =>
        _client.SetProperty("volume", Math.Clamp(volume, 0.0, 100.0));

    /// <summary>Mutes or unmutes without changing the volume.</summary>
    public void SetMuted(bool muted) => _client.SetProperty("mute", muted);

    /// <summary>Sets the playback rate, where 1.0 is normal.</summary>
    /// <remarks>
    /// <c>audio-pitch-correction</c> is on by libmpv default, so speech stays
    /// intelligible at 1.5× instead of turning into a chipmunk.
    /// </remarks>
    public void SetSpeed(double speed) =>
        _client.SetProperty("speed", Math.Clamp(speed, 0.25, 4.0));

    /// <summary>Selects a subtitle track, or turns subtitles off.</summary>
    /// <param name="trackId">The engine track id, or <see langword="null"/> for none.</param>
    public void SetSubtitleTrack(long? trackId) =>
        _client.SetProperty("sid", trackId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no");

    /// <summary>Selects an audio track.</summary>
    public void SetAudioTrack(long trackId) =>
        _client.SetProperty("aid", trackId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Adds an external subtitle file and selects it.</summary>
    public void AddSubtitleFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _client.Command("sub-add", path, "select");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _client.PropertyChanged -= OnPropertyChanged;
        _client.FileLoaded -= OnFileLoaded;
        _client.EndFile -= OnEndFile;
        _client.LogMessage -= OnLogMessage;
        _client.Shutdown -= OnShutdown;
        _client.Dispose();
    }

    /// <summary>
    /// Chooses the delivery format for an observed property.
    /// </summary>
    /// <remarks>
    /// Observing a numeric property as a string makes libmpv format it for
    /// display on every tick — several times a second for <c>time-pos</c> — and
    /// then the app parses it straight back. Asking for the native shape avoids
    /// both halves of that.
    /// </remarks>
    private static MpvFormat FormatFor(string property) => property switch
    {
        PlaybackSnapshotReducer.Properties.Paused
            or PlaybackSnapshotReducer.Properties.Muted
            or PlaybackSnapshotReducer.Properties.SeekingOrBuffering
            or PlaybackSnapshotReducer.Properties.PausedForCache => MpvFormat.Flag,

        PlaybackSnapshotReducer.Properties.Path
            or PlaybackSnapshotReducer.Properties.MediaTitle => MpvFormat.String,

        _ => MpvFormat.Double,
    };

    private void OnPropertyChanged(object? sender, MpvPropertyChangedEventArgs e) =>
        Mutate(current => PlaybackSnapshotReducer.Apply(current, e.Name, e.Value));

    private void OnFileLoaded(object? sender, EventArgs e)
    {
        // Ask rather than assume: the file may have been opened into a paused
        // state by a resume-position restore.
        bool paused = _client.GetFlag("pause") ?? false;
        Mutate(current => PlaybackSnapshotReducer.MarkLoaded(current, paused));
    }

    private void OnEndFile(object? sender, MpvEndFileEventArgs e)
    {
        if (e.Reason == MpvEndFileReason.Error)
        {
            var failure = new MpvException(e.Error, "playback");
            Mutate(current => PlaybackSnapshotReducer.MarkFailed(current, failure.Message));
            return;
        }

        if (!e.ReachedEnd)
        {
            // Stop and quit are the app's own doing; the snapshot is already
            // where it should be and advancing here would skip a file.
            return;
        }

        Mutate(PlaybackSnapshotReducer.MarkEnded);
        ReachedEnd?.Invoke(this, EventArgs.Empty);
    }

    private void OnLogMessage(object? sender, MpvLogEventArgs e) => LogMessage?.Invoke(this, e);

    private void OnShutdown(object? sender, EventArgs e) => Mutate(PlaybackSnapshotReducer.Reset);

    /// <summary>
    /// Applies a transform and publishes the result if it changed anything.
    /// </summary>
    /// <remarks>
    /// The equality check matters: libmpv re-reports <c>time-pos</c> several
    /// times a second, and a good fraction of those land on the same value once
    /// it has been rounded into a <see cref="TimeSpan"/>. Publishing every one
    /// would drive a layout pass per tick for no visible change.
    /// </remarks>
    private void Mutate(Func<PlaybackSnapshot, PlaybackSnapshot> transform)
    {
        PlaybackSnapshot updated;
        lock (_snapshotLock)
        {
            PlaybackSnapshot previous = _snapshot;
            updated = transform(previous);
            if (updated == previous)
            {
                return;
            }

            _snapshot = updated;
        }

        SnapshotChanged?.Invoke(this, updated);
    }
}
