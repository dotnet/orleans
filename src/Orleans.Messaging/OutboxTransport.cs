using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Messaging;

/// <summary>Calls a receiver's inbox for one outbox delivery attempt.</summary>
/// <remarks>Tests replace the transport to inject delivery faults.</remarks>
internal interface IOutboxTransport
{
    /// <summary>Delivers <paramref name="message"/> to the inbox of <paramref name="receiver"/>.</summary>
    ValueTask<DeliveryResult> DeliverAsync(GrainId receiver, InboxMessage message, CancellationToken cancellationToken);
}

/// <summary>Delivers outbox messages through grain calls to the receivers' messaging extensions.</summary>
internal sealed class GrainOutboxTransport(IGrainFactory grainFactory) : IOutboxTransport
{
    public ValueTask<DeliveryResult> DeliverAsync(GrainId receiver, InboxMessage message, CancellationToken cancellationToken) =>
        grainFactory.GetGrain<IMessagingExtension>(receiver).DeliverAsync(message, cancellationToken);
}
