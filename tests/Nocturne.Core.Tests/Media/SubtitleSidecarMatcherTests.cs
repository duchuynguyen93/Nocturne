using Nocturne.Core.Media;
using Xunit;

namespace Nocturne.Core.Tests.Media;

public sealed class SubtitleSidecarMatcherTests
{
    private static readonly string[] SeasonFolder =
    [
        "movie.mkv",
        "movie.srt",
        "movie.vi.srt",
        "movie.en-US.vtt",
        "movie-copy.srt",
        "movie 2.srt",
        "movie.2024.remux.srt",
        "another-movie.srt",
        "movie.txt",
    ];

    [Fact]
    public void The_bare_sidecar_is_offered_first()
    {
        IReadOnlyList<SubtitleSidecar> matches = SubtitleSidecarMatcher.Match("movie.mkv", SeasonFolder);

        Assert.Equal("movie.srt", matches[0].FileName);
        Assert.Null(matches[0].Language);
    }

    [Fact]
    public void Language_suffixed_sidecars_are_matched_and_tagged()
    {
        IReadOnlyList<SubtitleSidecar> matches = SubtitleSidecarMatcher.Match("movie.mkv", SeasonFolder);

        Assert.Contains(matches, m => m.FileName == "movie.vi.srt" && m.Language == "vi");
        Assert.Contains(matches, m => m.FileName == "movie.en-US.vtt" && m.Language == "en-US");
    }

    [Theory]
    [InlineData("movie-copy.srt")]
    [InlineData("movie 2.srt")]
    [InlineData("movie.2024.remux.srt")]
    [InlineData("another-movie.srt")]
    [InlineData("movie.txt")]
    public void Near_misses_are_rejected(string fileName)
    {
        IReadOnlyList<SubtitleSidecar> matches = SubtitleSidecarMatcher.Match("movie.mkv", SeasonFolder);

        // Attaching a neighbouring episode's subtitles is worse than attaching
        // none: the track menu fills with entries that are silently wrong.
        Assert.DoesNotContain(matches, m => m.FileName == fileName);
    }

    [Fact]
    public void Matching_is_case_insensitive_because_Windows_paths_are()
    {
        IReadOnlyList<SubtitleSidecar> matches =
            SubtitleSidecarMatcher.Match("Movie.MKV", ["MOVIE.SRT", "Movie.VI.srt"]);

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void An_extensionless_media_file_matches_nothing()
    {
        // Its stem equals its whole name, so a prefix rule would attach every
        // subtitle in the folder.
        Assert.Empty(SubtitleSidecarMatcher.Match("movie", SeasonFolder));
    }

    [Fact]
    public void The_order_is_stable_between_launches()
    {
        IReadOnlyList<SubtitleSidecar> first = SubtitleSidecarMatcher.Match("movie.mkv", SeasonFolder);
        // AsEnumerable first: on an array, Reverse() binds to Array.Reverse,
        // which sorts in place and returns void.
        IReadOnlyList<SubtitleSidecar> second =
            SubtitleSidecarMatcher.Match("movie.mkv", SeasonFolder.AsEnumerable().Reverse());

        Assert.Equal(first.Select(m => m.FileName), second.Select(m => m.FileName));
    }

    [Fact]
    public void A_blank_media_name_is_not_a_wildcard()
    {
        Assert.Empty(SubtitleSidecarMatcher.Match("   ", SeasonFolder));
        Assert.Empty(SubtitleSidecarMatcher.Match(string.Empty, SeasonFolder));
    }
}
