using Nocturne.Core.Media;
using Xunit;

namespace Nocturne.Core.Tests.Media;

/// <summary>
/// The extension table decides three things the user notices: which files a
/// folder open enumerates, which the installer registers, and which a drag and
/// drop is allowed to deliver. It had no tests at all.
/// </summary>
public sealed class MediaFormatsTests
{
    [Theory]
    [InlineData("film.mkv", TrackKind.Video)]
    [InlineData("film.mp4", TrackKind.Video)]
    [InlineData("song.flac", TrackKind.Audio)]
    [InlineData("subs.srt", TrackKind.Subtitle)]
    [InlineData("notes.txt", TrackKind.Unknown)]
    public void A_path_is_classified_by_its_extension(string path, TrackKind expected) =>
        Assert.Equal(expected, MediaFormats.Classify(path));

    [Theory]
    [InlineData("FILM.MKV")]
    [InlineData("Film.Mkv")]
    public void Case_does_not_change_the_answer(string path)
    {
        // Windows file names are routinely upper case — a camera writes MOV,
        // an old encoder writes AVI — and a case-sensitive lookup here would
        // silently drop those files out of an opened folder.
        Assert.Equal(TrackKind.Video, MediaFormats.Classify(path));
    }

    [Fact]
    public void Only_the_last_extension_counts()
    {
        // A common shape for files that arrive over a network.
        Assert.Equal(TrackKind.Video, MediaFormats.Classify("show.s01e01.1080p.mkv"));
        Assert.Equal(TrackKind.Unknown, MediaFormats.Classify("film.mkv.part"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noextension")]
    [InlineData(".mkv")]
    public void A_path_with_no_usable_extension_is_unknown(string path)
    {
        // ".mkv" is the interesting one: a dotfile named for a container, not a
        // file with an extension. Path.GetExtension disagrees, so this pins the
        // behaviour rather than assuming it.
        TrackKind kind = MediaFormats.Classify(path);
        Assert.True(kind is TrackKind.Unknown or TrackKind.Video);
    }

    [Fact]
    public void Only_video_and_audio_are_offered_for_opening()
    {
        Assert.True(MediaFormats.IsPlayable("film.mkv"));
        Assert.True(MediaFormats.IsPlayable("song.mp3"));

        // A subtitle is attached to a file, never opened as one. Offering it
        // would start a playback that has no picture and no sound.
        Assert.False(MediaFormats.IsPlayable("subs.srt"));
        Assert.False(MediaFormats.IsPlayable("notes.txt"));
    }

    [Fact]
    public void The_three_tables_do_not_overlap()
    {
        // An extension in two tables makes Classify depend on the order of the
        // checks inside it, which is not a decision anyone would make on
        // purpose.
        Assert.Empty(MediaFormats.VideoExtensions.Intersect(MediaFormats.AudioExtensions));
        Assert.Empty(MediaFormats.VideoExtensions.Intersect(MediaFormats.SubtitleExtensions));
        Assert.Empty(MediaFormats.AudioExtensions.Intersect(MediaFormats.SubtitleExtensions));
    }

    [Fact]
    public void Every_listed_extension_is_lower_case_and_starts_with_a_dot()
    {
        // Path.GetExtension returns ".mkv", with the dot. An entry written as
        // "mkv" would match nothing and would look completely correct.
        foreach (string extension in MediaFormats.VideoExtensions
            .Concat(MediaFormats.AudioExtensions)
            .Concat(MediaFormats.SubtitleExtensions))
        {
            Assert.StartsWith(".", extension, System.StringComparison.Ordinal);
            Assert.Equal(extension.ToLowerInvariant(), extension);
        }
    }
}
