using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Core;
using Orleans.Placement;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.Storage;

namespace Orleans.Streams;

internal interface IPullingAgentGrain : IGrain, IStreamProducerExtension
{
    Task<PullingAgentStatus> Probe(CancellationToken cancellationToken = default);
    Task<bool> Rebalance(GrainAddress expectedAddress, SiloAddress destination, CancellationToken cancellationToken = default);
    Task Stop(SiloAddress expectedHost, CancellationToken cancellationToken = default);
}

[GenerateSerializer]
internal readonly record struct PullingAgentStatus(
    [property: Id(0)] GrainAddress Address,
    [property: Id(1)] bool IsRunning);

[GrainType(PullingAgentId.GrainTypeName)]
[PullingAgentPlacement]
[Immovable]
internal sealed class PullingAgentGrain(
    PullingAgentRuntime runtime,
    PullingAgentHostResolver hosts,
    ILogger<PullingAgentGrain> logger) : Grain, IPullingAgentGrain
{
    internal static readonly GrainInterfaceType InterfaceType = GrainInterfaceType.Create("Orleans.Streams.IPullingAgentGrain");
    private PullingAgentRuntime.Provider _provider = null!;
    private string _providerName = null!;
    private QueueId _queueId;
    private volatile PersistentStreamPullingAgent? _agent;
    private Task _shutdownTask = Task.CompletedTask;
    private PullingAgentPublisherRegistry? _publishers;
    internal bool IsRunning => _agent is not null;
    internal int PubSubCacheSize => _agent?.PubSubCacheSize ?? 0;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var (providerName, queueId) = PullingAgentId.Parse(GrainContext.GrainId);
        _providerName = providerName;
        _queueId = queueId;
        _provider = runtime.GetProvider(providerName);
        if (_provider.DurablePubSub is { } pubSub)
        {
            var services = GrainContext.ActivationServices;
            var storage = services.GetKeyedService<IGrainStorage>(providerName)
                ?? services.GetRequiredKeyedService<IGrainStorage>(ProviderConstants.DEFAULT_PUBSUB_PROVIDER_NAME);
            _publishers = new(
                new StateStorageBridge<PullingAgentPublisherState>(nameof(PullingAgentPublisherState), GrainContext, storage),
                pubSub, GrainContext.GrainId, logger);
            await _publishers.Load(cancellationToken);
        }

        if (_provider.IsEligible(queueId))
        {
            await Start(cancellationToken);
        }
    }

    public async Task<PullingAgentStatus> Probe(CancellationToken cancellationToken)
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

    public async Task Stop(SiloAddress expectedHost, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expectedHost == GrainContext.Address.SiloAddress)
        {
            await StopCore(unregisterProducer: _publishers is null);
            if (_publishers is { } publishers)
            {
                await publishers.Retire();
            }
        }
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        // The stable producer registration serves the successor. Pub/sub callbacks route through migration.
        await StopCore(unregisterProducer: false);
        if (reason.ReasonCode == DeactivationReasonCode.ShuttingDown && !cancellationToken.IsCancellationRequested)
        {
            var survivors = (await hosts.GetEligibleSilos(
                _providerName, _queueId, PullingAgentId.GrainType, InterfaceType, cancellationToken))
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
            _publishers?.Open();
            agent.PublisherRegistry = _publishers;
            await agent.Initialize(cancellationToken, waitForReceiver: true);
            _shutdownTask = Task.CompletedTask;
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

    private Task StopCore(bool unregisterProducer)
    {
        if (_agent is not { } agent)
        {
            return _shutdownTask;
        }

        _agent = null;
        return _shutdownTask = ShutdownAgent(agent, unregisterProducer);
    }

    private async Task ShutdownAgent(PersistentStreamPullingAgent agent, bool unregisterProducer)
    {
        try
        {
            // Caller deadlines bound observation; admitted receiver cleanup completes under its own timeouts.
            await agent.Shutdown(CancellationToken.None, suppressReceiverShutdownErrors: false, unregisterProducer);
        }
        finally
        {
            try
            {
                if (_publishers is { } publishers)
                {
                    await publishers.Drain();
                }
            }
            finally
            {
                Unregister();
            }
        }
    }

    private void Unregister() => ((ICollection<KeyValuePair<QueueId, PullingAgentGrain>>)_provider.Agents)
        .Remove(new(_queueId, this));

    public Task AddSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, GrainId streamConsumer, string? filterData, CancellationToken cancellationToken)
        => _agent?.AddSubscriber(subscriptionId, streamId, streamConsumer, filterData, cancellationToken) ?? Task.CompletedTask;

    public Task RemoveSubscriber(GuidId subscriptionId, QualifiedStreamId streamId, CancellationToken cancellationToken)
        => _agent?.RemoveSubscriber(subscriptionId, streamId, cancellationToken) ?? Task.CompletedTask;
}
