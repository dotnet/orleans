using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.Messaging.Tests.Support;

public interface IRawPayloadTestGrain : IGrainWithGuidKey
{
    Task ConfigureForwardAsync(GrainId target, int copies = 1);
    Task<DeliveryResult> AcceptAndDeactivateAsync(InboxMessage message);
    Task<RawPayloadSnapshot> GetSnapshotAsync();
    Task RequestDeactivationAsync();
}

[GenerateSerializer]
public sealed record RawPayloadEffect([property: Id(0)] HierarchicalKey MessageId, [property: Id(1)] string Bytes, [property: Id(2)] int Count);

[GenerateSerializer]
public sealed record RawPayloadSnapshot([property: Id(0)] Guid ActivationId, [property: Id(1)] int InboxCount,
    [property: Id(2)] int OutboxCount, [property: Id(3)] int ProcessedCount, [property: Id(4)] RawPayloadEffect[] Effects);

// The application protocol is arbitrary bytes. Orleans codecs are only used for journal commands,
// business state and RPC results, never to encode/decode the messaging payload.
public sealed class RawPayloadTestGrain : DurableGrain, IRawPayloadTestGrain, IInboxHandler
{
    public const string HandlerBarrier = "raw-payload";
    private readonly Inbox _inbox;
    private readonly IOutbox _outbox;
    private readonly IDurableDictionary<HierarchicalKey, RawPayloadEffect> _effects;
    private readonly IDurableValue<GrainId> _forward;
    private readonly IDurableValue<int> _forwardCopies;
    internal int ExpectedAcknowledgedEffects { get; set; } = 1;
    private readonly HandlerProbe _handlers;
    private readonly Guid _activationId = Guid.NewGuid();
    private readonly TaskCompletionSource<RawPayloadSnapshot> _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<RawPayloadSnapshot> Acknowledged => _acknowledged.Task;

    public RawPayloadTestGrain(IGrainContext context, IOutbox outbox, IJournaledStateManager manager,
        [FromKeyedServices("raw-effects")] IDurableDictionary<HierarchicalKey, RawPayloadEffect> effects,
        [FromKeyedServices("raw-forward")] IDurableValue<GrainId> forward,
        [FromKeyedServices("raw-forward-copies")] IDurableValue<int> forwardCopies,
        HandlerProbe handlers)
    {
        _inbox = context.ActivationServices.GetRequiredService<Inbox>();
        _outbox = outbox;
        _effects = effects;
        _forward = forward;
        _forwardCopies = forwardCopies;
        _handlers = handlers;
        _inbox.Register(this);
        new JournalProbeState(manager).WriteCompleted = () =>
        {
            if (_inbox.Count == 0 && _outbox.Count() == 0 && _effects.Count == ExpectedAcknowledgedEffects)
            {
                _acknowledged.TrySetResult(GetSnapshotForTest());
            }
        };
    }

    public async Task ConfigureForwardAsync(GrainId target, int copies = 1)
    {
        _forward.Value = target;
        _forwardCopies.Value = copies;
        await WriteStateAsync();
    }

    public async Task<DeliveryResult> AcceptAndDeactivateAsync(InboxMessage message)
    {
        var result = await ((IInboxExtension)_inbox).DeliverAsync(message);
        AcceptedSnapshot = GetSnapshotForTest();
        DeactivateOnIdle();
        return result;
    }

    public bool CanHandle(IInboxHandlerContext context) => true;

    public async ValueTask HandleAsync(IInboxHandlerContext context, CancellationToken cancellationToken)
    {
        if (_handlers.TryGet(this.GetGrainId(), HandlerBarrier, out var barrier))
        {
            barrier.Entered.TrySetResult();
            await barrier.Continue.Task.WaitAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var input = context.Message.Envelope;
        var bytes = Convert.ToBase64String(input.Payload);
        var outgoing = _forward.Value.IsDefault
            ? []
            : Enumerable.Range(0, _forwardCopies.Value).Select(index => new OutboxMessage
            {
                Envelope = index == 0
                    ? input
                    : input with { MessageId = input.MessageId.CreateChildKey(index.ToString(CultureInfo.InvariantCulture)) },
                ReceiverId = _forward.Value
            }).ToArray();
        _effects.TryGetValue(input.MessageId, out var prior);
        _effects[input.MessageId] = new RawPayloadEffect(input.MessageId, bytes, (prior?.Count ?? 0) + 1);
        foreach (var envelope in outgoing)
        {
            _outbox.Send(envelope);
        }
        context.Complete();
    }

    internal RawPayloadSnapshot? AcceptedSnapshot { get; private set; }

    public Task<RawPayloadSnapshot> GetSnapshotAsync() => Task.FromResult(GetSnapshotForTest());
    internal RawPayloadSnapshot GetSnapshotForTest() => new(_activationId, _inbox.Count, _outbox.Count(), InboxTestAccess.GetProcessedCount(_inbox), _effects.Values.ToArray());
    public Task RequestDeactivationAsync()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }
}
