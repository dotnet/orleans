using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Orleans.Runtime;

namespace Orleans.Messaging.Tests.Support;

public sealed class HandlerProbe
{
    private readonly ConcurrentDictionary<(GrainId GrainId, string Route), Barrier> _barriers = new();

    public Barrier Arm(GrainId grainId, string route, bool observeCancellation = true)
    {
        var barrier = new Barrier(this, (grainId, route), observeCancellation);
        if (!_barriers.TryAdd((grainId, route), barrier))
        {
            throw new InvalidOperationException($"A handler barrier is already armed for '{grainId}' and route '{route}'.");
        }

        return barrier;
    }

    public bool TryGet(
        GrainId grainId,
        string route,
        [NotNullWhen(true)] out Barrier? barrier) =>
        _barriers.TryGetValue((grainId, route), out barrier);

    public sealed class Barrier : IDisposable
    {
        private readonly HandlerProbe _owner;
        private readonly (GrainId GrainId, string Route) _key;
        private readonly bool _observeCancellation;

        internal Barrier(HandlerProbe owner, (GrainId GrainId, string Route) key, bool observeCancellation)
        {
            _owner = owner;
            _key = key;
            _observeCancellation = observeCancellation;
        }

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilEnteredAsync() => Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        public void Release() => Continue.TrySetResult();

        // A barrier that ignores cancellation holds the handler until Release, even after shutdown.
        internal Task WaitAsync(CancellationToken cancellationToken) =>
            _observeCancellation ? Continue.Task.WaitAsync(cancellationToken) : Continue.Task;

        public void Dispose()
        {
            Release();
            _owner._barriers.TryRemove(new KeyValuePair<(GrainId GrainId, string Route), Barrier>(_key, this));
        }
    }
}
