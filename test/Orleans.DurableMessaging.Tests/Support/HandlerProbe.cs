using System.Collections.Concurrent;
using Orleans.Runtime;

namespace Orleans.DurableMessaging.Tests.Support;

public sealed class HandlerProbe
{
    private readonly ConcurrentDictionary<(GrainId GrainId, string Route), Barrier> _barriers = new();

    public Barrier Arm(GrainId grainId, string route)
    {
        var barrier = new Barrier(this, (grainId, route));
        if (!_barriers.TryAdd((grainId, route), barrier))
        {
            throw new InvalidOperationException($"A handler barrier is already armed for '{grainId}' and route '{route}'.");
        }

        return barrier;
    }

    public bool TryGet(GrainId grainId, string route, out Barrier barrier) =>
        _barriers.TryGetValue((grainId, route), out barrier!);

    public sealed class Barrier : IDisposable
    {
        private readonly HandlerProbe _owner;
        private readonly (GrainId GrainId, string Route) _key;

        internal Barrier(HandlerProbe owner, (GrainId GrainId, string Route) key)
        {
            _owner = owner;
            _key = key;
        }

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilEnteredAsync() => Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        public void Release() => Continue.TrySetResult();

        public void Dispose()
        {
            Release();
            _owner._barriers.TryRemove(new KeyValuePair<(GrainId GrainId, string Route), Barrier>(_key, this));
        }
    }
}
