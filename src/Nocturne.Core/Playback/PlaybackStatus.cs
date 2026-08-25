namespace Nocturne.Core.Playback;

/// <summary>Lifecycle stage of the current item.</summary>
public enum PlaybackStatus
{
    /// <summary>No file loaded.</summary>
    Idle,

    /// <summary>A file was requested and the engine is opening it.</summary>
    Opening,

    /// <summary>Frames are being presented.</summary>
    Playing,

    /// <summary>A file is loaded and the playhead is held still.</summary>
    Paused,

    /// <summary>The playhead reached the end of the file.</summary>
    Ended,

    /// <summary>The item could not be played; see <see cref="PlaybackSnapshot.ErrorMessage"/>.</summary>
    Failed,
}
