namespace Clircs.ConsoleClient;

internal sealed class InputHistory(int capacity = 500)
{
    private readonly List<string> _entries = [];
    private int _index;
    private string _draft = string.Empty;

    public void Begin()
    {
        _index = _entries.Count;
        _draft = string.Empty;
    }

    public void Commit(string value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            (_entries.Count == 0 || !string.Equals(_entries[^1], value, StringComparison.Ordinal)))
        {
            _entries.Add(value);
            if (_entries.Count > capacity) _entries.RemoveAt(0);
        }
        Begin();
    }

    public string? Previous(string current)
    {
        if (_entries.Count == 0) return null;
        if (_index == _entries.Count) _draft = current;
        if (_index > 0) _index--;
        return _entries[_index];
    }

    public string? Next()
    {
        if (_index >= _entries.Count) return null;
        _index++;
        return _index == _entries.Count ? _draft : _entries[_index];
    }
}
