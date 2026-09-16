using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling;

namespace Orleans.DurableMessaging.Tests.Support;

// Captures handler output in the same journal as inbox effects. Dispatch belongs to the outbox layer.
internal sealed class JournaledTestOutbox(
    [FromKeyedServices("test-handler-output")] IDurableDictionary<Guid, DurableEnvelope> messages) : IDurableOutbox
{
    public int Count => messages.Count;
    public IEnumerable<DurableEnvelope> Messages => messages.Values;
    public void Send(DurableEnvelope envelope) => messages.Add(envelope.MessageId, envelope);
    public bool TryGetMessage(Guid messageId, [MaybeNullWhen(false)] out DurableEnvelope envelope) =>
        messages.TryGetValue(messageId, out envelope);
}
