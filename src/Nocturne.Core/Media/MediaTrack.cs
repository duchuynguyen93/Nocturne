namespace Nocturne.Core.Media;

/// <summary>Which stream of a container a track belongs to.</summary>
public enum TrackKind
{
    /// <summary>Unrecognised or unsupported stream type.</summary>
    Unknown,

    /// <summary>A video stream.</summary>
    Video,

    /// <summary>An audio stream.</summary>
    Audio,

    /// <summary>A subtitle stream, embedded or attached from a sidecar file.</summary>
    Subtitle,
}

/// <summary>
/// One selectable stream inside the current item.
/// </summary>
/// <param name="Id">
/// The engine's own track id. This is what a track-selection command is issued
/// against, and it is not an index into any list the UI holds.
/// </param>
/// <param name="Kind">Which stream type this track belongs to.</param>
/// <param name="Title">Track title from container metadata, if the muxer wrote one.</param>
/// <param name="Language">RFC 5646-ish language tag from the container, if present.</param>
/// <param name="Codec">Short codec name, for the technical detail row.</param>
/// <param name="IsDefault">Whether the container marks this as its default track.</param>
/// <param name="IsForced">Whether the container marks this subtitle track as forced.</param>
/// <param name="IsExternal">Whether the track came from a sidecar file rather than the container.</param>
public sealed record MediaTrack(
    long Id,
    TrackKind Kind,
    string? Title,
    string? Language,
    string? Codec,
    bool IsDefault = false,
    bool IsForced = false,
    bool IsExternal = false)
{
    /// <summary>
    /// The label shown in the track menu.
    /// </summary>
    /// <remarks>
    /// Containers are inconsistent about what they populate: some write only a
    /// language, some only a title, plenty write neither. Falling through those
    /// cases in order avoids menu entries that read as a bare "Track 3" when the
    /// file did carry something useful, and avoids a blank entry when it did not.
    /// </remarks>
    public string DisplayLabel
    {
        get
        {
            bool hasTitle = !string.IsNullOrWhiteSpace(Title);
            bool hasLanguage = !string.IsNullOrWhiteSpace(Language);

            string baseLabel = (hasTitle, hasLanguage) switch
            {
                (true, true) => $"{Title!.Trim()} ({Language!.Trim()})",
                (true, false) => Title!.Trim(),
                (false, true) => Language!.Trim(),
                (false, false) => $"{KindLabel} {Id}",
            };

            return IsForced ? $"{baseLabel} · forced" : baseLabel;
        }
    }

    private string KindLabel => Kind switch
    {
        TrackKind.Video => "Video",
        TrackKind.Audio => "Audio",
        TrackKind.Subtitle => "Subtitle",
        _ => "Track",
    };
}
