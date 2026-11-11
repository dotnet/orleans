using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Messaging;

[GenerateSerializer]
public sealed record ReserveStock(
    [property: Id(1)] int Quantity,
    [property: Id(2)] GrainId ReplyDestination);

[GenerateSerializer]
public sealed record Restock([property: Id(0)] int Quantity);

[GenerateSerializer]
public abstract record ReservationOutcome(
    [property: Id(0)] MessageId CommandId,
    [property: Id(1)] int Quantity,
    [property: Id(4)] int RemainingStock);

[GenerateSerializer]
public sealed record ReservationAccepted(MessageId CommandId, int Quantity, int RemainingStock)
    : ReservationOutcome(CommandId, Quantity, RemainingStock);

[GenerateSerializer]
public sealed record ReservationRejected(
    MessageId CommandId, int Quantity, int RemainingStock,
    [property: Id(0)] ReservationRejectionReason Reason)
    : ReservationOutcome(CommandId, Quantity, RemainingStock);

public enum ReservationRejectionReason
{
    InvalidQuantity,
    InsufficientStock
}

[GenerateSerializer]
public sealed record Inventory(
    [property: Id(0)] int Remaining,
    [property: Id(1)] int Reservations,
    [property: Id(2)] int ProcessedRequests);

[GenerateSerializer]
public sealed record StockSnapshot(
    [property: Id(0)] Inventory Inventory);

public sealed class StockMessages(Serializer serializer)
{
    public MessageType<ReserveStock> Reserve { get; } = new("inventory.reserve.v1", serializer);
    public MessageType<Restock> Restock { get; } = new("inventory.restock.v1", serializer);
    public MessageType<ReservationOutcome> Result { get; } = new("inventory.reservation-result.v1", serializer);
}
