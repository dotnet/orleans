using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Streams;

internal interface IPullingAgentRuntime : ISystemTarget
{
    Task<bool> IsEligible(string providerName, QueueId? queueId, CancellationToken cancellationToken = default);
    Task<bool> CanRetire(string providerName, QueueId queueId, CancellationToken cancellationToken = default);
}

internal sealed class PullingAgentRuntime : SystemTarget, IPullingAgentRuntime, ILifecycleParticipant<ISiloLifecycle>
{
    internal static readonly GrainType TargetType = SystemTargetGrainId.CreateGrainType("stream-pulling-agent-runtime");
    private readonly ConcurrentDictionary<string, Provider> _providers = new(StringComparer.Ordinal);

    public PullingAgentRuntime(SystemTargetShared shared) : base(TargetType, shared)
    {
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    internal void Register(string name, Provider provider)
    {
        if (!_providers.TryAdd(name, provider))
        {
            throw new InvalidOperationException($"The pulling-agent runtime for stream provider '{name}' is already initialized.");
        }
    }

    internal Provider GetProvider(string name) => _providers.TryGetValue(name, out var provider)
        ? provider
        : throw new OrleansException($"The pulling-agent runtime for stream provider '{name}' is not initialized on {Silo}.");

    public Task<bool> IsEligible(string providerName, QueueId? queueId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_providers.TryGetValue(providerName, out var provider)
            && (queueId is { } queue
                ? provider.IsEligible(queue)
                : provider.State == StreamLifecycleOptions.RunState.AgentsStarted));
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
    }

    public Task<bool> CanRetire(string providerName, QueueId queueId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_providers.TryGetValue(providerName, out var provider)
            && provider.State == StreamLifecycleOptions.RunState.AgentsStopped
            && provider.Queues.Contains(queueId));
    }

    internal sealed class Provider(
        ImmutableHashSet<QueueId> queues,
        StreamPullingAgentOptions options,
        Func<IGrainContext, QueueId, Task<PersistentStreamPullingAgent>> createAgent)
    {
        private readonly object _lifecycleLock = new();
        private volatile StreamLifecycleOptions.RunState _state;
        internal StreamLifecycleOptions.RunState State
        {
            get => _state;
            set
            {
                lock (_lifecycleLock)
                {
                    _state = value;
                }
            }
        }
        internal ImmutableHashSet<QueueId> Queues { get; } = queues;
        internal StreamPullingAgentOptions Options { get; } = options;
        internal IStreamPubSub? DurablePubSub { get; init; }
        internal ConcurrentDictionary<QueueId, PullingAgentGrain> Agents { get; } = new();
        internal int RunningAgentCount => Agents.Count(static entry => entry.Value.IsRunning);
        internal QueueId[] GetRunningQueues() => Agents.Where(static entry => entry.Value.IsRunning).Select(static entry => entry.Key).ToArray();
        internal bool IsEligible(QueueId queueId) => State == StreamLifecycleOptions.RunState.AgentsStarted && Queues.Contains(queueId);
        internal Task<PersistentStreamPullingAgent> CreateAgent(IGrainContext context, QueueId queueId) => createAgent(context, queueId);

        internal bool TryRegisterAgent(QueueId queueId, PullingAgentGrain agent)
        {
            lock (_lifecycleLock)
            {
                if (!IsEligible(queueId))
                {
                    return false;
                }

                Agents[queueId] = agent;
                return true;
            }
        }
    }
}
