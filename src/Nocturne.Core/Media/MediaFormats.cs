namespace Nocturne.Core.Media;

/// <summary>
/// The file extensions the app claims it can open.
/// </summary>
/// <remarks>
/// This list does not decide what actually plays — FFmpeg probes content and
/// will happily open a correct file with a wrong extension. It decides three
/// narrower things: which files the folder-open command enumerates, which
/// extensions the installer registers, and which drag-and-drop payloads are
/// accepted. Being conservative here is safe; being wrong is not, because a
/// registered extension the engine cannot play makes Windows blame the app.
/// </remarks>
public static class MediaFormats
{
    /// <summary>Container extensions treated as video, lower case, with the dot.</summary>
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".webm", ".wmv", ".flv",
        ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".m2v", ".vob", ".ogv",
        ".3gp", ".rmvb", ".divx", ".mxf",
    };

    /// <summary>Container extensions treated as audio, lower case, with the dot.</summary>
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma",
        ".alac", ".ape", ".wv", ".dsf", ".dff", ".mka",
    };

    /// <summary>Sidecar subtitle extensions, lower case, with the dot.</summary>
    public static readonly IReadOnlySet<string> SubtitleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".ttml",
    };

    /// <summary>Whether the app should offer to open this path.</summary>
    public static bool IsPlayable(string path) =>
        Classify(path) is TrackKind.Video or TrackKind.Audio;

    /// <summary>
    /// Classifies a path by extension alone.
    /// </summary>
    /// <returns>
    /// <see cref="TrackKind.Unknown"/> for anything not listed, including paths
    /// with no extension at all.
    /// </returns>
    public static TrackKind Classify(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return TrackKind.Unknown;
        }

        string extension = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            return TrackKind.Unknown;
        }

        if (VideoExtensions.Contains(extension))
        {
            return TrackKind.Video;
        }

        if (AudioExtensions.Contains(extension))
        {
            return TrackKind.Audio;
        }

        return SubtitleExtensions.Contains(extension) ? TrackKind.Subtitle : TrackKind.Unknown;
    }
}
