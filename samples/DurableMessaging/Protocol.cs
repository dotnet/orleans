using Orleans;
using Orleans.DurableMessaging;
using Orleans.Runtime;

namespace DurableMessaging;

[GenerateSerializer]
public sealed record ReserveStock(
    [property: Id(1)] int Quantity,
    [property: Id(2)] GrainId ReplyDestination);

[GenerateSerializer]
public sealed record ReservationOutcome(
    [property: Id(0)] HierarchicalKey CommandId,
    [property: Id(1)] int Quantity,
    [property: Id(2)] bool Accepted,
    [property: Id(4)] int RemainingStock);

[GenerateSerializer]
public sealed record Inventory(
    [property: Id(0)] int Remaining,
    [property: Id(1)] int Reservations,
    [property: Id(2)] int ProcessedRequests);

[GenerateSerializer]
public sealed record StockSnapshot(
    [property: Id(0)] Inventory Inventory);

internal static class StockProtocol
{
    public const string Reserve = "inventory.reserve.v1";
    public const string Result = "inventory.reservation-result.v1";
}
