using System.Text.RegularExpressions;
using Nocturne.Core.Media;
using Xunit;

namespace Nocturne.Core.Tests.Media;

/// <summary>
/// The installer's file associations must match <see cref="MediaFormats"/>.
/// </summary>
/// <remarks>
/// <para>
/// The two lists are written in different languages in different files and there
/// is no build step that connects them, so they drift silently and the symptom
/// is invisible from inside the app: an extension the app can play is missing
/// from the "Open with" menu, or — worse — one it cannot play is offered there
/// and Windows blames Nocturne when it fails.
/// </para>
/// <para>
/// Reading the installer script from a unit test is unusual, and the
/// alternative is worse: a hand-maintained list of 34 extensions repeated three
/// times, checked by nobody.
/// </para>
/// </remarks>
public sealed class InstallerAssociationTests
{
    /// <summary>The three registry mechanisms, each keyed by how to find it.</summary>
    /// <remarks>
    /// All three are required and they are not interchangeable. Shipping only
    /// the capability entry is what made the first release's associations appear
    /// to do nothing at all.
    /// </remarks>
    public static TheoryData<string, string> Mechanisms => new()
    {
        { "Capabilities\\FileAssociations", @"FileAssociations"".*?ValueName: ""(\.[a-z0-9]+)""" },
        { "<ext>\\OpenWithProgIds", @"Software\\Classes\\(\.[a-z0-9]+)\\OpenWithProgIds" },
        { "Applications\\<exe>\\SupportedTypes", @"SupportedTypes"".*?ValueName: ""(\.[a-z0-9]+)""" },
    };

    [Theory]
    [MemberData(nameof(Mechanisms))]
    public void The_installer_registers_exactly_what_the_app_can_play(string mechanism, string pattern)
    {
        string script = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "Nocturne.iss"));

        HashSet<string> registered = Regex
            .Matches(script, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups[1].Value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> playable = MediaFormats.VideoExtensions
            .Concat(MediaFormats.AudioExtensions)
            .Select(extension => extension.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        Assert.False(
            registered.Count == 0,
            $"{mechanism}: no extensions matched. The installer's layout changed and this " +
            "test is now checking nothing, which is worse than failing.");

        Assert.Empty(playable.Except(registered));
        Assert.Empty(registered.Except(playable));
    }

    [Fact]
    public void The_installer_does_not_claim_subtitle_files()
    {
        // A subtitle is attached to a file, never opened as one. Registering it
        // puts Nocturne in the "Open with" menu for a file it would open to a
        // black screen with no sound.
        string script = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "Nocturne.iss"));

        foreach (string extension in MediaFormats.SubtitleExtensions)
        {
            Assert.DoesNotContain(
                $"ValueName: \"{extension}\"",
                script,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Walks up from the test assembly until the solution file appears.
    /// </summary>
    /// <remarks>
    /// The relative depth from the output directory to the repository root
    /// depends on the target framework and configuration in the path, so it is
    /// searched for rather than counted.
    /// </remarks>
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nocturne.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"No Nocturne.sln above {AppContext.BaseDirectory}.");
    }
}
