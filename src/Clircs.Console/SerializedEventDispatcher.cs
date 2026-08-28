namespace Clircs.ConsoleClient;

// Preserves one complete ordering boundary around application event delivery.
// The lock is deliberately reentrant because one event may produce a related
// local event, such as a highlight echo, before the original delivery finishes.
internal sealed class SerializedEventDispatcher<T>(Action<T> handler)
{
    private readonly object _gate = new();
    private bool _completed;

    public bool Dispatch(T item)
    {
        lock (_gate)
        {
            if (_completed) return false;
            handler(item);
            return true;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
        }
    }
}
