using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableMessaging.Tests.Support;

// Captures opaque handler output in the same journal as inbox effects. Dispatch belongs to the outbox layer.
internal sealed class JournaledTestOutbox(IDurableDictionary<HierarchicalKey, DurableEnvelope> messages, IGrainContext context)
    : IDurableOutbox, ILifecycleObserver, IEnumerable<KeyValuePair<HierarchicalKey, DurableEnvelope>>
{
    private bool _stopped;
    private readonly TaskCompletionSource _stopping = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public GrainId SenderId => context.GrainId;
    public Task Stopping => _stopping.Task;
    public int SendCalls { get; private set; }
    public Exception? NextSendFailure { get; set; }
    public IDurableDictionary<HierarchicalKey, DurableEnvelope> StoredMessages { get; } = messages;
    public int Count => StoredMessages.Count;
    public IEnumerable<DurableEnvelope> Messages => StoredMessages.Values;
    public IEnumerator<KeyValuePair<HierarchicalKey, DurableEnvelope>> GetEnumerator() => StoredMessages.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Send(DurableEnvelope envelope)
    {
        SendCalls++;
        if (_stopped) throw new InvalidOperationException("The test outbox owner is stopped.");
        if (envelope.MessageId.IsDefault || envelope.SenderId.IsDefault || envelope.ReceiverId.IsDefault
            || string.IsNullOrEmpty(envelope.Subject))
        {
            throw new ArgumentException("An outgoing envelope requires identities and opaque payload bytes.", nameof(envelope));
        }
        if (TryGetMessage(envelope.MessageId, out var existing))
        {
            if (existing.SenderId != envelope.SenderId || existing.ReceiverId != envelope.ReceiverId
                || !string.Equals(existing.Subject, envelope.Subject, StringComparison.Ordinal)
                || !existing.Payload.ToArray().AsSpan().SequenceEqual(envelope.Payload.ToArray()))
            {
                throw new InvalidOperationException($"The durable outbox already contains a different envelope with message ID '{envelope.MessageId}'.");
            }
            return;
        }
        if (NextSendFailure is { } failure)
        {
            NextSendFailure = null;
            throw failure;
        }
        StoredMessages.Add(envelope.MessageId, envelope);
    }

    public bool TryGetMessage(HierarchicalKey messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope) =>
        StoredMessages.TryGetValue(messageId, out envelope);
    public Task OnStart(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task OnStop(CancellationToken cancellationToken)
    {
        _stopped = true;
        _stopping.TrySetResult();
        return Task.CompletedTask;
    }
}
