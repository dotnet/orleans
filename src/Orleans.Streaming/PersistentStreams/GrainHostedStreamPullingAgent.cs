using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Streams;

internal interface IGrainHostedStreamPullingAgent : IGrain, IStreamProducerExtension
{
    Task<StreamPullingAgentStatus> Probe(CancellationToken cancellationToken = default);
    Task<bool> Rebalance(GrainAddress expectedAddress, SiloAddress destination, CancellationToken cancellationToken = default);
    Task Stop(SiloAddress expectedHost, CancellationToken cancellationToken = default);
}

[GenerateSerializer]
internal readonly record struct StreamPullingAgentStatus(
    [property: Id(0)] GrainAddress Address,
    [property: Id(1)] bool IsRunning);

[GrainType(StreamPullingAgentId.GrainTypeName)]
[StreamPullingAgentPlacement]
[Immovable]
internal sealed class GrainHostedStreamPullingAgent(
    StreamPullingAgentRuntime runtime,
    StreamPullingAgentHostResolver hosts,
    ILogger<GrainHostedStreamPullingAgent> logger) : Grain, IGrainHostedStreamPullingAgent
{
    internal static readonly GrainInterfaceType InterfaceType = GrainInterfaceType.Create("Orleans.Streams.IGrainHostedStreamPullingAgent");
    private StreamPullingAgentRuntime.Provider _provider = null!;
    private string _providerName = null!;
    private QueueId _queueId;
    private volatile PersistentStreamPullingAgent? _agent;
    internal bool IsRunning => _agent is not null;
    internal int PubSubCacheSize => _agent?.PubSubCacheSize ?? 0;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var (providerName, queueId) = StreamPullingAgentId.Parse(GrainContext.GrainId);
        _providerName = providerName;
        _queueId = queueId;
        _provider = runtime.GetProvider(providerName);
        if (_provider.IsEligible(queueId))
        {
            await Start(cancellationToken);
        }
    }

    public async Task<StreamPullingAgentStatus> Probe(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_agent is null && _provider.IsEligible(_queueId))
        {
            await Start(cancellationToken);
        }

        return new(GrainContext.Address, IsRunning);
    }

    public Task<bool> Rebalance(GrainAddress expectedAddress, SiloAddress destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GrainContext.Address.Equals(expectedAddress) || destination == GrainContext.Address.SiloAddress)
        {
            return Task.FromResult(false);
        }

        GrainContext.Migrate(new Dictionary<string, object>
        {
            [IPlacementDirector.PlacementHintKey] = destination,
        }, CancellationToken.None);
        return Task.FromResult(true);
    }

    public Task Stop(SiloAddress expectedHost, CancellationToken cancellationToken)
        => expectedHost == GrainContext.Address.SiloAddress
            ? StopCore(cancellationToken, unregisterProducer: true)
            : Task.CompletedTask;

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        // The stable producer registration serves the successor. Pub/sub callbacks route through migration.
        await StopCore(cancellationToken, unregisterProducer: false);
        if (reason.ReasonCode == DeactivationReasonCode.ShuttingDown && !cancellationToken.IsCancellationRequested)
        {
            var survivors = (await hosts.GetEligibleSilos(
                _providerName, _queueId, StreamPullingAgentId.GrainType, InterfaceType, cancellationToken))
                .Where(silo => silo != GrainContext.Address.SiloAddress).ToArray();
            if (survivors.Length > 0)
            {
                GrainContext.Migrate(new Dictionary<string, object>
                {
                    [IPlacementDirector.PlacementHintKey] = survivors[Random.Shared.Next(survivors.Length)],
                }, CancellationToken.None);
            }
        }
    }

    private async Task Start(CancellationToken cancellationToken)
    {
        // Register before awaiting initialization so provider stop also drains activations being started by callbacks.
        if (!_provider.TryRegisterAgent(_queueId, this))
        {
            return;
        }

        PersistentStreamPullingAgent? agent = null;
        try
        {
            agent = await _provider.CreateAgent(GrainContext, _queueId);
            await agent.Initialize(cancellationToken, waitForReceiver: true);
            _agent = agent;
        }
        catch
        {
            try
            {
                if (agent is not null)
                {
                    await agent.Shutdown(CancellationToken.None, suppressReceiverShutdownErrors: false, unregisterProducer: false);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to clean up pulling agent {GrainId} after initialization failed.", GrainContext.GrainId);
            }
            finally
            {
                Unregister();
            }

            throw;
        }
    }

    private async Task StopCore(CancellationToken cancellationToken, bool unregisterProducer)
    {
        if (_agent is not { } agent)
        {
            return;
        }

        _agent = null;
        try
        {
            await agent.Shutdown(cancellationToken, suppressReceiverShutdownErrors: false, unregisterProducer);
        }
        finally
        {
            Unregister();
        }
    }

    private void Unregister() => ((ICollection<KeyValuePair<QueueId, GrainHostedStreamPullingAgent>>)_provider.Agents)
        .Remove(new(_queueId, this));

    public Task AddSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData, CancellationToken cancellationToken)
        => _agent?.AddSubscriber(subscriptionId, streamId, streamConsumer, filterData, cancellationToken) ?? Task.CompletedTask;

    public Task RemoveSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, CancellationToken cancellationToken)
        => _agent?.RemoveSubscriber(subscriptionId, streamId, cancellationToken) ?? Task.CompletedTask;
}
