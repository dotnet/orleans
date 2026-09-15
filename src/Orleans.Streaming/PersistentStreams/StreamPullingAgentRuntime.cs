using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.Streams;

internal interface IStreamPullingAgentRuntime : ISystemTarget
{
    Task<bool> IsEligible(string providerName, QueueId queueId, CancellationToken cancellationToken = default);
}

internal sealed class StreamPullingAgentRuntime : SystemTarget, IStreamPullingAgentRuntime, ILifecycleParticipant<ISiloLifecycle>
{
    internal static readonly GrainType TargetType = SystemTargetGrainId.CreateGrainType("stream-pulling-agent-runtime");
    private readonly ConcurrentDictionary<string, Provider> _providers = new(StringComparer.Ordinal);

    public StreamPullingAgentRuntime(SystemTargetShared shared) : base(TargetType, shared)
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

    public Task<bool> IsEligible(string providerName, QueueId queueId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_providers.TryGetValue(providerName, out var provider) && provider.IsEligible(queueId));
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
    }

    internal sealed class Provider(Func<IGrainContext, QueueId, Task<PersistentStreamPullingAgent>> createAgent)
    {
        internal volatile StreamLifecycleOptions.RunState State;
        internal ImmutableHashSet<QueueId> DesiredQueues
        {
            get => Volatile.Read(ref _desiredQueues);
            set => Volatile.Write(ref _desiredQueues, value);
        }

        private ImmutableHashSet<QueueId> _desiredQueues = ImmutableHashSet<QueueId>.Empty;
        internal ConcurrentDictionary<QueueId, GrainHostedStreamPullingAgent> Agents { get; } = new();
        internal bool IsEligible(QueueId queueId) => State == StreamLifecycleOptions.RunState.AgentsStarted && DesiredQueues.Contains(queueId);
        internal Task<PersistentStreamPullingAgent> CreateAgent(IGrainContext context, QueueId queueId) => createAgent(context, queueId);
    }
}
