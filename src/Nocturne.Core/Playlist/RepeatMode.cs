namespace Nocturne.Core.Playlist;

/// <summary>What happens when the last item finishes.</summary>
public enum RepeatMode
{
    /// <summary>Stop after the final item.</summary>
    Off,

    /// <summary>Wrap from the final item back to the first.</summary>
    All,

    /// <summary>Replay the current item indefinitely.</summary>
    One,
}
