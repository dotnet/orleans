using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.DurableMessaging.Tests.Support;

public interface INamedFactoryMessagingGrain : IGrainWithGuidKey
{
    Task SendAsync(GrainId target, string route, DurableTestMessage message);
    Task<NamedFactoryMessagingSnapshot> GetSnapshotAsync();
    Task RequestDeactivationAsync();
}

[GenerateSerializer]
public sealed record NamedFactoryMessagingSnapshot(
    [property: Id(0)] Guid ActivationId,
    [property: Id(1)] int OutboxCount,
    [property: Id(2)] DurableEffect[] Effects);

public sealed class NamedFactoryMessagingProbe
{
    private readonly ConcurrentDictionary<GrainId, TaskCompletionSource<NamedFactoryMessagingSnapshot>> _completed = new();

    public Task<NamedFactoryMessagingSnapshot> WaitAsync(GrainId grainId, CancellationToken cancellationToken) =>
        GetCompletion(grainId).Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

    public void Publish(GrainId grainId, NamedFactoryMessagingSnapshot snapshot)
    {
        if (snapshot.Effects.Length > 0 && snapshot.OutboxCount == 0)
        {
            GetCompletion(grainId).TrySetResult(snapshot);
        }
    }

    private TaskCompletionSource<NamedFactoryMessagingSnapshot> GetCompletion(GrainId grainId) =>
        _completed.GetOrAdd(grainId, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
}

public sealed class NamedFactoryMessagingGrain : Grain, INamedFactoryMessagingGrain, IDurableMessagingGrain, IInboxHandler
{
    private readonly IJournaledStateManager _owner;
    private readonly IDurableOutbox _outbox;
    private readonly IDurableDictionary<Guid, DurableEffect> _effects;
    private readonly SerializerSessionPool _sessions;
    private readonly NamedFactoryMessagingProbe _probe;
    private readonly Guid _activationId = Guid.NewGuid();

    public NamedFactoryMessagingGrain(
        IJournaledStateManager owner,
        IDurableInbox inbox,
        IDurableOutbox outbox,
        [FromKeyedServices("cutover-effects")] IDurableDictionary<Guid, DurableEffect> effects,
        SerializerSessionPool sessions,
        NamedFactoryMessagingProbe probe)
    {
        _owner = owner;
        _outbox = outbox;
        _effects = effects;
        _sessions = sessions;
        _probe = probe;
        inbox.RegisterHandler(this);
    }

    public async Task SendAsync(GrainId target, string route, DurableTestMessage message)
    {
        var envelope = TestApplicationProtocol.Create(_sessions, this.GetGrainId(), target, route, message);
        _outbox.Send(envelope);
        await _owner.WriteStateAsync();
    }

    public Task<NamedFactoryMessagingSnapshot> GetSnapshotAsync() => Task.FromResult(CreateSnapshot());

    internal NamedFactoryMessagingSnapshot CaptureStorageWrite() => CreateSnapshot();
    internal void PublishStoredSnapshot(NamedFactoryMessagingSnapshot snapshot) => _probe.Publish(this.GetGrainId(), snapshot);

    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }


    public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        var message = Assert.IsType<DurableTestMessage>(TestApplicationProtocol.Read(_sessions, context.Envelope).Body);
        DurableEnvelope? outgoing = null;
        if (message.ForwardTo is { } target)
        {
            outgoing = TestApplicationProtocol.Create(_sessions, this.GetGrainId(), target, "messages/forwarded", message with { ForwardTo = null });
        }

        cancellationToken.ThrowIfCancellationRequested();
        _effects.TryGetValue(message.LogicalId, out var prior);
        var effect = new DurableEffect(message.LogicalId, (prior?.Count ?? 0) + 1, message.Sequence, message.Value);
        _effects[message.LogicalId] = effect;
        if (outgoing is { } batch)
        {
            _outbox.Send(batch);
        }
        context.Complete();
    }

    private NamedFactoryMessagingSnapshot CreateSnapshot() =>
        new(_activationId, _outbox.Count, _effects.Values.OrderBy(static effect => effect.Sequence).ToArray());
}
