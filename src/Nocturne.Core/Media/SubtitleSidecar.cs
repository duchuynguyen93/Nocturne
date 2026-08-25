using System.Diagnostics.CodeAnalysis;

namespace Nocturne.Core.Media;

/// <summary>A subtitle file sitting beside the media file it belongs to.</summary>
/// <param name="FileName">File name of the sidecar, without any directory part.</param>
/// <param name="Language">
/// The language tag lifted from the name, or <see langword="null"/> when the
/// sidecar carries no suffix.
/// </param>
public readonly record struct SubtitleSidecar(string FileName, string? Language);

/// <summary>
/// Finds the sidecar subtitle files that belong to a media file.
/// </summary>
/// <remarks>
/// The matching rule is deliberately strict. Given <c>movie.mkv</c>, both
/// <c>movie.srt</c> and <c>movie.vi.srt</c> belong to it, but <c>movie-copy.srt</c>
/// and <c>movie 2.srt</c> do not. A looser prefix match would attach every
/// subtitle of every episode in a season folder to whichever episode was opened,
/// which is worse than attaching none: the track menu fills with entries that
/// are silently wrong rather than obviously missing.
/// <para>
/// Matching is a pure function over file names so it can be tested without a
/// filesystem, and so the caller keeps control of how the directory is
/// enumerated — which matters on network shares where enumeration can stall.
/// </para>
/// </remarks>
public static class SubtitleSidecarMatcher
{
    /// <summary>
    /// Selects the sidecars belonging to <paramref name="mediaFileName"/>.
    /// </summary>
    /// <param name="mediaFileName">
    /// File name of the media item, without a directory part.
    /// </param>
    /// <param name="siblingFileNames">
    /// File names found in the same directory. The media file itself may be
    /// included; it is never returned as its own subtitle.
    /// </param>
    /// <returns>
    /// The matching sidecars, ordered so unsuffixed files come first — a bare
    /// <c>movie.srt</c> is the author's default and should be the first track
    /// offered — and language-suffixed files follow in ordinal order so the
    /// track menu is stable between launches.
    /// </returns>
    public static IReadOnlyList<SubtitleSidecar> Match(
        string mediaFileName,
        IEnumerable<string> siblingFileNames)
    {
        ArgumentNullException.ThrowIfNull(siblingFileNames);

        if (!TryGetStem(mediaFileName, out string? stem))
        {
            return [];
        }

        var matches = new List<SubtitleSidecar>();
        foreach (string candidate in siblingFileNames)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string extension = System.IO.Path.GetExtension(candidate);
            if (!MediaFormats.SubtitleExtensions.Contains(extension))
            {
                continue;
            }

            string candidateStem = System.IO.Path.GetFileNameWithoutExtension(candidate);

            if (candidateStem.Equals(stem, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new SubtitleSidecar(candidate, Language: null));
                continue;
            }

            // Only a dot may separate the stem from a language tag. Requiring it
            // is what keeps "movie-copy" and "movie 2" out.
            if (candidateStem.Length > stem.Length + 1
                && candidateStem.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                && candidateStem[stem.Length] == '.')
            {
                string language = candidateStem[(stem.Length + 1)..];

                // A suffix containing a further dot is not a language tag —
                // "movie.2024.remux.srt" is a different release, not Vietnamese.
                // "en-US" and "pt-BR" stay valid because they use a hyphen.
                if (!language.Contains('.', StringComparison.Ordinal))
                {
                    matches.Add(new SubtitleSidecar(candidate, language));
                }
            }
        }

        matches.Sort(static (left, right) =>
        {
            bool leftBare = left.Language is null;
            bool rightBare = right.Language is null;
            if (leftBare != rightBare)
            {
                return leftBare ? -1 : 1;
            }

            return string.CompareOrdinal(left.FileName, right.FileName);
        });

        return matches;
    }

    private static bool TryGetStem(string mediaFileName, [NotNullWhen(true)] out string? stem)
    {
        stem = null;
        if (string.IsNullOrWhiteSpace(mediaFileName))
        {
            return false;
        }

        string candidate = System.IO.Path.GetFileNameWithoutExtension(mediaFileName);

        // An extensionless media file has a stem equal to its whole name, which
        // would make every subtitle in the folder a prefix match.
        if (candidate.Length == 0 || candidate.Length == mediaFileName.Length)
        {
            return false;
        }

        stem = candidate;
        return true;
    }
}
