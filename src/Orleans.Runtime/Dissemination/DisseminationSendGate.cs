using System.Diagnostics.CodeAnalysis;

namespace Orleans.Runtime.Dissemination;

internal sealed class DisseminationSendGate(int maxConcurrency)
{
    private readonly object _lock = new();
    private readonly HashSet<SiloAddress> _activePeers = [];
    private readonly LinkedList<Request> _requests = [];
    private bool _stopped;

    public ValueTask<Lease> AcquireAsync(SiloAddress peer, CancellationToken cancellationToken) =>
        AcquireAsync(peer, onQueued: null, cancellationToken);

    public ValueTask<Lease> AcquireAsync(SiloAddress peer, Action? onQueued, CancellationToken cancellationToken)
    {
        Request request;
        lock (_lock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_stopped, this);
            // Release fills available slots before unlocking, so any remaining queued peers are busy.
            if (_activePeers.Count < maxConcurrency && _activePeers.Add(peer))
            {
                return ValueTask.FromResult(new Lease(this, peer));
            }

            request = new(this, peer);
            request.Node = _requests.AddLast(request);
        }

        return WaitAsync(request, onQueued, cancellationToken);
    }

    public bool TryAcquire(SiloAddress peer, [NotNullWhen(true)] out Lease? lease)
    {
        lock (_lock)
        {
            if (!_stopped && _activePeers.Count < maxConcurrency && _activePeers.Add(peer))
            {
                lease = new(this, peer);
                return true;
            }
        }

        lease = null;
        return false;
    }

    public void Stop()
    {
        lock (_lock)
        {
            _stopped = true;
            while (_requests.First is { } node)
            {
                _requests.Remove(node);
                node.Value.Completion.TrySetCanceled();
            }
        }
    }

    private static async ValueTask<Lease> WaitAsync(Request request, Action? onQueued, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.UnsafeRegister(
            static (state, token) =>
            {
                var request = (Request)state!;
                request.Owner.Cancel(request, token);
            },
            request);
        try
        {
            onQueued?.Invoke();
        }
        catch
        {
            // A diagnostic failure must not leave an orphaned request or a concurrently granted lease.
            request.Owner.Cancel(request, cancellationToken);
            if (request.Completion.Task.IsCompletedSuccessfully)
            {
                request.Completion.Task.Result.Dispose();
            }

            throw;
        }

        var lease = await request.Completion.Task.ConfigureAwait(false);
        // A grant can win the lock just before cancellation removes its request.
        if (cancellationToken.IsCancellationRequested)
        {
            lease.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return lease;
    }

    private void Cancel(Request request, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (request.Node is { List: not null } node)
            {
                _requests.Remove(node);
                request.Completion.TrySetCanceled(cancellationToken);
            }
        }
    }

    private void Release(SiloAddress peer)
    {
        lock (_lock)
        {
            _activePeers.Remove(peer);
            if (_stopped)
            {
                return;
            }

            // FIFO among ready destinations: skip peers which already have an active local attempt.
            var node = _requests.First;
            while (node is not null && _activePeers.Count < maxConcurrency)
            {
                var next = node.Next;
                if (_activePeers.Add(node.Value.Peer))
                {
                    _requests.Remove(node);
                    node.Value.Completion.SetResult(new Lease(this, node.Value.Peer));
                }

                node = next;
            }
        }
    }

    private sealed class Request(DisseminationSendGate owner, SiloAddress peer)
    {
        public DisseminationSendGate Owner { get; } = owner;
        public SiloAddress Peer { get; } = peer;
        public TaskCompletionSource<Lease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Request>? Node { get; set; }
    }

    internal sealed class Lease(DisseminationSendGate owner, SiloAddress peer) : IDisposable
    {
        private DisseminationSendGate? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(peer);
    }
}
