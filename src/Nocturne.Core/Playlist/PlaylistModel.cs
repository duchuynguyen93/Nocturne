namespace Nocturne.Core.Playlist;

/// <summary>
/// The ordered set of items the player will move through.
/// </summary>
/// <remarks>
/// Two orders are kept at once: the order the user sees and can reorder, and the
/// order playback actually follows. With shuffle off they are the same list.
/// With shuffle on, the visible order never changes — the play order does. This
/// is the distinction most implementations get wrong by shuffling the visible
/// list in place, which loses the user's arrangement the moment shuffle is
/// turned off again.
/// </remarks>
public sealed class PlaylistModel
{
    private readonly List<string> _items = [];

    /// <summary>Indices into <see cref="_items"/>, in the order playback follows.</summary>
    private readonly List<int> _playOrder = [];

    /// <summary>Position within <see cref="_playOrder"/>, or -1 when nothing is current.</summary>
    private int _playCursor = -1;

    private bool _isShuffled;

    /// <summary>The items in the order the user arranged them.</summary>
    public IReadOnlyList<string> Items => _items;

    /// <summary>What happens at the end of the queue.</summary>
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    /// <summary>Whether playback follows a shuffled order.</summary>
    public bool IsShuffled => _isShuffled;

    /// <summary>Index of the current item within <see cref="Items"/>, or -1.</summary>
    public int CurrentIndex => _playCursor >= 0 && _playCursor < _playOrder.Count
        ? _playOrder[_playCursor]
        : -1;

    /// <summary>The current item, or <see langword="null"/> when the queue is empty.</summary>
    public string? Current
    {
        get
        {
            int index = CurrentIndex;
            return index >= 0 ? _items[index] : null;
        }
    }

    /// <summary>Number of queued items.</summary>
    public int Count => _items.Count;

    /// <summary>
    /// Replaces the queue contents and selects <paramref name="startIndex"/>.
    /// </summary>
    /// <param name="items">New contents, in user-visible order.</param>
    /// <param name="startIndex">
    /// Item to make current. Out-of-range values select the first item, which is
    /// what "open this folder" should do when the requested file has since been
    /// deleted.
    /// </param>
    public void Load(IEnumerable<string> items, int startIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(items);

        _items.Clear();
        _items.AddRange(items);

        if (_items.Count == 0)
        {
            _playOrder.Clear();
            _playCursor = -1;
            return;
        }

        int resolved = startIndex >= 0 && startIndex < _items.Count ? startIndex : 0;
        RebuildPlayOrder(currentItemIndex: resolved);
    }

    /// <summary>
    /// Turns shuffle on or off without changing which item is playing.
    /// </summary>
    /// <param name="shuffled">Desired shuffle state.</param>
    /// <param name="random">
    /// Source of randomness. Supplied by the caller so playback order is
    /// reproducible under test rather than depending on ambient global state.
    /// </param>
    public void SetShuffled(bool shuffled, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        if (_isShuffled == shuffled)
        {
            return;
        }

        _isShuffled = shuffled;
        if (_items.Count == 0)
        {
            return;
        }

        RebuildPlayOrder(currentItemIndex: CurrentIndex, random);
    }

    /// <summary>
    /// Selects an item by its position in the user-visible list.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the index was in range and is now current.
    /// </returns>
    public bool SelectIndex(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return false;
        }

        int cursor = _playOrder.IndexOf(index);
        if (cursor < 0)
        {
            return false;
        }

        _playCursor = cursor;
        return true;
    }

    /// <summary>
    /// Advances to the next item under the current repeat mode.
    /// </summary>
    /// <param name="userInitiated">
    /// Whether the move came from the Next button rather than from an item
    /// finishing. This is the difference that <see cref="RepeatMode.One"/> turns
    /// on: repeat-one replays the file when it ends, but pressing Next must
    /// still move forward, otherwise the button appears broken.
    /// </param>
    /// <returns>The new current item, or <see langword="null"/> when the queue ends.</returns>
    public string? MoveNext(bool userInitiated = false)
    {
        if (_playOrder.Count == 0)
        {
            return null;
        }

        if (Repeat == RepeatMode.One && !userInitiated)
        {
            return Current;
        }

        if (_playCursor + 1 < _playOrder.Count)
        {
            _playCursor++;
            return Current;
        }

        if (Repeat is RepeatMode.All or RepeatMode.One)
        {
            _playCursor = 0;
            return Current;
        }

        return null;
    }

    /// <summary>
    /// Moves to the previous item, wrapping only when repeat covers the queue.
    /// </summary>
    /// <returns>The new current item, or <see langword="null"/> at the start.</returns>
    public string? MovePrevious()
    {
        if (_playOrder.Count == 0)
        {
            return null;
        }

        if (_playCursor - 1 >= 0)
        {
            _playCursor--;
            return Current;
        }

        if (Repeat is RepeatMode.All or RepeatMode.One)
        {
            _playCursor = _playOrder.Count - 1;
            return Current;
        }

        return null;
    }

    /// <summary>
    /// Whether <see cref="MoveNext"/> would land on an item rather than stop.
    /// </summary>
    public bool HasNext => _playOrder.Count > 0
        && (_playCursor + 1 < _playOrder.Count || Repeat is RepeatMode.All or RepeatMode.One);

    /// <summary>
    /// Whether <see cref="MovePrevious"/> would land on an item rather than stop.
    /// </summary>
    public bool HasPrevious => _playOrder.Count > 0
        && (_playCursor > 0 || Repeat is RepeatMode.All or RepeatMode.One);

    private void RebuildPlayOrder(int currentItemIndex, Random? random = null)
    {
        _playOrder.Clear();
        for (int i = 0; i < _items.Count; i++)
        {
            _playOrder.Add(i);
        }

        if (_isShuffled)
        {
            Random source = random ?? new Random();

            // Fisher-Yates over the whole list, then lift the current item to the
            // front. Pinning it first means turning shuffle on mid-file does not
            // interrupt what is playing, and the remaining order stays uniform
            // because the swap only moves an already-uniform arrangement.
            for (int i = _playOrder.Count - 1; i > 0; i--)
            {
                int j = source.Next(i + 1);
                (_playOrder[i], _playOrder[j]) = (_playOrder[j], _playOrder[i]);
            }

            if (currentItemIndex >= 0)
            {
                int position = _playOrder.IndexOf(currentItemIndex);
                if (position > 0)
                {
                    (_playOrder[0], _playOrder[position]) = (_playOrder[position], _playOrder[0]);
                }
            }

            _playCursor = currentItemIndex >= 0 ? 0 : -1;
            return;
        }

        _playCursor = currentItemIndex >= 0 && currentItemIndex < _playOrder.Count
            ? currentItemIndex
            : -1;
    }
}
