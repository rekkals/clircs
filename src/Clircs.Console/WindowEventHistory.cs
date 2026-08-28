using System.Collections;
using Clircs.Sessions;

namespace Clircs.ConsoleClient;

/// <summary>
/// Per-window event storage with constant-time append and removal from the oldest end.
/// IRC floods routinely age history from the front, which makes List.RemoveRange an
/// expensive array shift for every event once the emergency ceiling is reached.
/// </summary>
internal sealed class WindowEventHistory : IList<SessionEvent>, IReadOnlyList<SessionEvent>
{
    private SessionEvent?[] _items = new SessionEvent?[16];
    private int _head;

    public int Count { get; private set; }

    public bool IsReadOnly => false;

    public SessionEvent this[int index]
    {
        get => _items[PhysicalIndex(index)]!;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _items[PhysicalIndex(index)] = value;
        }
    }

    public void Add(SessionEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureCapacity(Count + 1);
        _items[(_head + Count) % _items.Length] = item;
        Count++;
    }

    public void AddRange(IEnumerable<SessionEvent> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items) Add(item);
    }

    public void RemoveFirst(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > Count) throw new ArgumentOutOfRangeException(nameof(count));
        for (var index = 0; index < count; index++)
        {
            _items[(_head + index) % _items.Length] = null;
        }
        _head = (_head + count) % _items.Length;
        Count -= count;
        if (Count == 0) _head = 0;
    }

    public void Clear()
    {
        Array.Clear(_items);
        _head = 0;
        Count = 0;
    }

    public bool Contains(SessionEvent item) => IndexOf(item) >= 0;

    public void CopyTo(SessionEvent[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (arrayIndex < 0 || arrayIndex + Count > array.Length)
            throw new ArgumentOutOfRangeException(nameof(arrayIndex));
        for (var index = 0; index < Count; index++) array[arrayIndex + index] = this[index];
    }

    public int IndexOf(SessionEvent item)
    {
        for (var index = 0; index < Count; index++)
        {
            if (EqualityComparer<SessionEvent>.Default.Equals(this[index], item)) return index;
        }
        return -1;
    }

    public void Insert(int index, SessionEvent item)
    {
        if ((uint)index > (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == Count)
        {
            Add(item);
            return;
        }
        ArgumentNullException.ThrowIfNull(item);
        EnsureCapacity(Count + 1);
        Add(this[Count - 1]);
        for (var current = Count - 2; current > index; current--) this[current] = this[current - 1];
        this[index] = item;
    }

    public bool Remove(SessionEvent item)
    {
        var index = IndexOf(item);
        if (index < 0) return false;
        RemoveAt(index);
        return true;
    }

    public void RemoveAt(int index)
    {
        _ = PhysicalIndex(index);
        if (index == 0)
        {
            RemoveFirst(1);
            return;
        }
        for (var current = index; current < Count - 1; current++) this[current] = this[current + 1];
        var tail = (_head + Count - 1) % _items.Length;
        _items[tail] = null;
        Count--;
    }

    public IEnumerator<SessionEvent> GetEnumerator()
    {
        for (var index = 0; index < Count; index++) yield return this[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private int PhysicalIndex(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return (_head + index) % _items.Length;
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _items.Length) return;
        var expanded = new SessionEvent?[Math.Max(required, _items.Length * 2)];
        for (var index = 0; index < Count; index++) expanded[index] = this[index];
        _items = expanded;
        _head = 0;
    }
}
