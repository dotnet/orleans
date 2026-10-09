using Orleans;
using Orleans.DurableMessaging;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;

namespace DurableMessaging;

// The application protocol defines message kinds, business keys, and reply destinations.
[GenerateSerializer]
public abstract record StockMessage;

[GenerateSerializer]
public sealed record ReserveStock(
    [property: Id(0)] HierarchicalKey Operation,
    [property: Id(1)] int Quantity,
    [property: Id(2)] GrainId ReplyDestination) : StockMessage;

[GenerateSerializer]
public sealed record ReservationOutcome(
    [property: Id(0)] HierarchicalKey Operation,
    [property: Id(1)] int Quantity,
    [property: Id(2)] bool Accepted,
    [property: Id(3)] Guid ReservationId,
    [property: Id(4)] int RemainingStock) : StockMessage;

[GenerateSerializer]
public sealed record Inventory(
    [property: Id(0)] int Remaining,
    [property: Id(1)] int Reservations,
    [property: Id(2)] int ProcessedRequests);

[GenerateSerializer]
public sealed record StockSnapshot(
    [property: Id(0)] Inventory Inventory,
    [property: Id(1)] int LedgerEntries);

internal static class StockProtocol
{
    // Each activation owns and reuses its encoder. The returned envelope owns the consumed slice.
    public static DurableEnvelope Encode(
        Serializer<StockMessage> serializer, ArcBufferWriter encoder,
        GrainId sender, GrainId receiver, StockMessage message)
    {
        if (sender.IsDefault)
        {
            throw new ArgumentException("A sender grain ID is required.", nameof(sender));
        }
        if (receiver.IsDefault)
        {
            throw new ArgumentException("A receiver grain ID is required.", nameof(receiver));
        }
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            serializer.Serialize(message, encoder);
            return new()
            {
                MessageId = Guid.NewGuid(),
                SenderId = sender,
                ReceiverId = receiver,
                Payload = encoder.ConsumeSlice(encoder.Length)
            };
        }
        catch
        {
            encoder.Reset();
            throw;
        }
    }
}
