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
    private readonly Guid _activationId = Guid.NewGuid();
    private NamedFactoryMessagingSnapshot? _captured;

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
        var state = (ObservedJournalDictionary<Guid, DurableEffect>)effects;
        state.Capturing = () => _captured = CreateSnapshot();
        state.Written = () => probe.Publish(this.GetGrainId(), Assert.IsType<NamedFactoryMessagingSnapshot>(_captured));
        inbox.RegisterHandler(this);
    }

    public async Task SendAsync(GrainId target, string route, DurableTestMessage message)
    {
        var envelope = new DurableEnvelopeBuilder(_sessions, this.GetGrainId()).To(target, route).WithBody(message).Build();
        using var batch = await _outbox.PrepareSendAsync([envelope]);
        _outbox.Send(batch);
        await _owner.WriteStateAsync();
    }

    public Task<NamedFactoryMessagingSnapshot> GetSnapshotAsync() => Task.FromResult(CreateSnapshot());

    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public bool CanHandle(IInboxHandlerContext context) => true;

    public async ValueTask<Action> PrepareAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        Assert.True(context.Envelope.Data.TryGetBody<DurableTestMessage>(out var body));
        var message = Assert.IsType<DurableTestMessage>(body);
        IPreparedOutboxBatch? outgoing = null;
        if (message.ForwardTo is { } target)
        {
            var envelope = context.CreateEnvelope().To(target, "messages/forwarded").WithBody(message with { ForwardTo = null }).Build();
            outgoing = await context.Outbox.PrepareSendAsync([envelope], cancellationToken);
        }

        return () =>
        {
            _effects.TryGetValue(message.LogicalId, out var prior);
            _effects[message.LogicalId] = new DurableEffect(message.LogicalId, (prior?.Count ?? 0) + 1, message.Sequence, message.Value);
            if (outgoing is { } batch)
            {
                context.Send(batch);
            }
        };
    }

    private NamedFactoryMessagingSnapshot CreateSnapshot() =>
        new(_activationId, _outbox.Count, _effects.Values.OrderBy(static effect => effect.Sequence).ToArray());
}
