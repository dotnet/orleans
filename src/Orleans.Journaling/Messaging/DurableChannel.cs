#if false
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling.Messaging;

[GenerateSerializer, DebuggerDisplay("{MessageId}")]
internal readonly struct IdempotencyKey : IEquatable<IdempotencyKey>
{
    [Id(0)]
    public Guid Value { get; init; }

    public bool Equals(IdempotencyKey other) => other.Value == Value;

    public override bool Equals(object? obj) => obj is IdempotencyKey other && Equals(other);

    public override int GetHashCode() => Value.GetHashCode();

    public static bool operator ==(IdempotencyKey left, IdempotencyKey right) => left.Equals(right);

    public static bool operator !=(IdempotencyKey left, IdempotencyKey right) => !(left == right);

    public override string ToString() => Value.ToString();
}

[GenerateSerializer, Immutable]
internal struct InboxMessage
{
    [Id(0)]
    public GrainId SenderId { get; set; }

    [Id(1)]
    public IdempotencyKey MessageId { get; set; }

    [Id(2)]
    public object MessageBody { get; set; }

    [Id(3)]
    public Dictionary<string, object?>? RequestContext { get; set; }
}
    
[GenerateSerializer, Immutable]
internal struct OutboxMessage
{
    [Id(0)]
    public GrainId ReceiverId { get; set; }

    [Id(1)]
    public IdempotencyKey MessageId { get; set; }

    [Id(2)]
    public object MessageBody { get; set; }

    [Id(3)]
    public Dictionary<string, object?>? RequestContext { get; set; }
}

internal interface IDurableMessageChannelGrainExtension : IGrainExtension
{
    ValueTask AddToInboxAsync(InboxMessage message, CancellationToken cancellationToken);
}

[GenerateSerializer]
internal enum ProcessingState
{
    Received,
    Processing,
    Processed,
    Failed
}

[GenerateSerializer]
internal struct MessageProcessingState
{
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
    public List<ProcessingHistoryItem> ProcessingHistory { get; set; }
    public int ProcessingAttempts { get; set; }
}

internal struct ProcessingHistoryItem
{
    public DateTimeOffset Timestamp { get; set; }
    public string Text { get; set; }
}

internal sealed class DurableMessageChannelGrainExtension : IDurableMessageChannelGrainExtension, ILifecycleParticipant<IGrainLifecycle>
{
    private readonly IGrainContext _grainContext;
    private readonly IStateMachineManager _stateMachineManager;
    private readonly IDurableDictionary<(GrainId SenderId, IdempotencyKey MessageId), OutboxMessage> _outbox;
    private readonly IDurableQueue<InboxMessage> _inbox;
    private readonly IDurableDictionary<(GrainId SenderId, IdempotencyKey MessageId), MessageProcessingState> _processingState;

    public DurableMessageChannelGrainExtension(
        IGrainContext grainContext,
        IStateMachineManager stateMachineManager,
        [FromKeyedServices("outbox")] IDurableQueue<OutboxMessage> outbox,
        [FromKeyedServices("inbox")] IDurableQueue<InboxMessage> inbox,
        [FromKeyedServices("processing-state")] IDurableDictionary<(GrainId, IdempotencyKey), MessageProcessingState> processingState)
    {
        _grainContext = grainContext;
        _stateMachineManager = stateMachineManager;
        _outbox = outbox;
        _inbox = inbox;
        _processingState = processingState;
    }

    public void EnqueueOutboxMessage(OutboxMessage message)
    {
        _outbox.TryAdd(message);
    }

    public ValueTask AddToInboxAsync(InboxMessage message, CancellationToken cancellationToken)
    {
        var key = (message.SenderId, message.MessageId);
        if (_processingState.TryGetValue(key, out var existing))
        {
            // Already exists, this is a duplicate.
            return;
        }

        // TODO: if grain is not busy, and message handler is atomic, process the message immediately, without adding it to inbox.
        //       This is an optimization to avoid an unnecessary write, cutting cost from 2 writes to 1 write.
        //       Easiest way to check for atomicity currently is by checking if the message derives from `VoidRequest`, since they cannot be async.
        if (message.MessageBody is VoidRequest request)
        {
            ProcessMessageAsync(message, cancellationToken);
            // return a promise that is completed when the message is processed.
            throw null!;
        }
        else
        {
            _inbox.Enqueue(message);
        }

        return _stateMachineManager.WriteStateAsync(cancellationToken);

        await _processingState.SetAsync(message.MessageId, message);
        await _stateMachineManager.WriteStateAsync(cancellationToken);
    }

    public void Participate(IGrainLifecycle lifecycle)
    {
    }
}
#endif
