using Clircs.Identity;

namespace Clircs.ConsoleClient;

// The ordinary flood path is deliberately lossless. This is the last-resort boundary
// for an input stream that is outliving the resources available to retain it. One
// incident is reported per connection; reconnecting resets the circuit.
internal sealed class InboundResourceCircuitBreaker
{
    private readonly object _gate = new();
    private readonly HashSet<NetworkSessionId> _openCircuits = [];

    public bool TryOpen(NetworkSessionId sessionId)
    {
        lock (_gate) return _openCircuits.Add(sessionId);
    }

    public void Reset(NetworkSessionId sessionId)
    {
        lock (_gate) _openCircuits.Remove(sessionId);
    }
}
