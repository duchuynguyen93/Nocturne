using Nocturne.Core.Playlist;
using Xunit;

namespace Nocturne.Core.Tests.Playlist;

public sealed class PlaylistModelTests
{
    private static readonly string[] ThreeItems = ["a.mkv", "b.mkv", "c.mkv"];

    private static PlaylistModel Loaded(int startIndex = 0)
    {
        var queue = new PlaylistModel();
        queue.Load(ThreeItems, startIndex);
        return queue;
    }

    [Fact]
    public void An_out_of_range_start_index_falls_back_to_the_first_item()
    {
        // "Open this folder at that file" when the file was deleted meanwhile.
        PlaylistModel queue = Loaded(startIndex: 99);

        Assert.Equal("a.mkv", queue.Current);
    }

    [Fact]
    public void Repeat_off_stops_at_the_end()
    {
        PlaylistModel queue = Loaded(startIndex: 2);

        Assert.Null(queue.MoveNext());
        Assert.Equal("c.mkv", queue.Current);
        Assert.False(queue.HasNext);
    }

    [Fact]
    public void Repeat_all_wraps_in_both_directions()
    {
        PlaylistModel queue = Loaded(startIndex: 2);
        queue.Repeat = RepeatMode.All;

        Assert.Equal("a.mkv", queue.MoveNext());
        Assert.Equal("c.mkv", queue.MovePrevious());
    }

    [Fact]
    public void Repeat_one_replays_on_completion_but_still_honours_the_Next_button()
    {
        PlaylistModel queue = Loaded();
        queue.Repeat = RepeatMode.One;

        Assert.Equal("a.mkv", queue.MoveNext());
        Assert.Equal("b.mkv", queue.MoveNext(userInitiated: true));
    }

    [Fact]
    public void Turning_shuffle_on_does_not_interrupt_the_current_item()
    {
        PlaylistModel queue = Loaded(startIndex: 1);

        queue.SetShuffled(true, new Random(Seed: 1234));

        Assert.Equal("b.mkv", queue.Current);
        Assert.True(queue.IsShuffled);
    }

    [Fact]
    public void Shuffle_does_not_disturb_the_order_the_user_arranged()
    {
        PlaylistModel queue = Loaded();

        queue.SetShuffled(true, new Random(Seed: 7));

        Assert.Equal(ThreeItems, queue.Items);
    }

    [Fact]
    public void Turning_shuffle_off_restores_the_visible_order_for_playback()
    {
        PlaylistModel queue = Loaded(startIndex: 2);
        queue.SetShuffled(true, new Random(Seed: 7));
        queue.SetShuffled(false, new Random(Seed: 7));

        Assert.Equal("c.mkv", queue.Current);
        Assert.Equal(2, queue.CurrentIndex);
        Assert.Null(queue.MoveNext());
    }

    [Fact]
    public void A_shuffled_run_visits_every_item_exactly_once()
    {
        PlaylistModel queue = Loaded();
        queue.SetShuffled(true, new Random(Seed: 99));

        var visited = new List<string> { queue.Current! };
        while (queue.MoveNext() is { } next)
        {
            visited.Add(next);
        }

        Assert.Equal(ThreeItems.Length, visited.Count);
        Assert.Equal(ThreeItems.OrderBy(x => x), visited.OrderBy(x => x));
    }

    [Fact]
    public void An_empty_queue_never_produces_an_item()
    {
        var queue = new PlaylistModel();
        queue.Load([]);

        Assert.Null(queue.Current);
        Assert.Equal(-1, queue.CurrentIndex);
        Assert.Null(queue.MoveNext());
        Assert.Null(queue.MovePrevious());
        Assert.False(queue.HasNext);
        Assert.False(queue.HasPrevious);
    }

    [Fact]
    public void Selecting_by_visible_index_works_while_shuffled()
    {
        PlaylistModel queue = Loaded();
        queue.SetShuffled(true, new Random(Seed: 3));

        Assert.True(queue.SelectIndex(2));
        Assert.Equal("c.mkv", queue.Current);
        Assert.False(queue.SelectIndex(42));
    }
}
