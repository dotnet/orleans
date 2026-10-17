using System.Text.Json.Serialization;
using Orleans.Messaging;
using Orleans.Serialization;

namespace OrderProcessing;

// The order flow's message protocol: each binding pairs a subject with its payload type.
// Grains inject this singleton to send messages and register handlers.
public sealed class OrderMessages(Serializer serializer)
{
    public MessageType<SubmitOrder> SubmitOrder { get; } = new("ordering.submit-order.v1", serializer);

    public MessageType<GracePeriodElapsed> GracePeriodElapsed { get; } = new("ordering.grace-period-elapsed.v1", serializer);

    public MessageType<OrderSummary> StatusChanged { get; } = new("ordering.status-changed.v1", serializer);

    public MessageType<ReserveStock> ReserveStock { get; } = new("catalog.reserve-stock.v1", serializer);

    public MessageType<StockResult> StockResult { get; } = new("catalog.stock-result.v1", serializer);

    public MessageType<ReleaseStock> ReleaseStock { get; } = new("catalog.release-stock.v1", serializer);

    public MessageType<CommitStock> CommitStock { get; } = new("catalog.commit-stock.v1", serializer);

    public MessageType<AuthorizePayment> AuthorizePayment { get; } = new("payments.authorize.v1", serializer);

    public MessageType<PaymentResult> PaymentResult { get; } = new("payments.result.v1", serializer);
}

public enum OrderStatus
{
    Submitted,
    AwaitingValidation,
    StockConfirmed,
    Paid,
    Shipped,
    Cancelled,
}

public enum StockLineStatus
{
    Pending,
    Confirmed,
    Rejected,
    Released,
    Committed,
}

[GenerateSerializer, Immutable]
public sealed record Address(string Street, string City, string State, string Country, string ZipCode);

[GenerateSerializer, Immutable]
public sealed record OrderLine(int ProductId, string ProductName, decimal UnitPrice, int Units);

[GenerateSerializer, Immutable]
public sealed record CatalogItem(
    int Id,
    string Type,
    string Brand,
    string Name,
    decimal Price,
    int AvailableStock,
    int ReservedStock,
    int SoldStock);

[GenerateSerializer, Immutable]
public sealed record BasketItem(int ProductId, string ProductName, decimal UnitPrice, int Quantity);

[GenerateSerializer, Immutable]
public sealed record CheckoutRequest(Guid RequestId, Address Address, string CardNumber);

[GenerateSerializer, Immutable]
public sealed record OrderState(
    Guid OrderId,
    string BuyerId,
    int Version,
    OrderStatus Status,
    DateTimeOffset OrderDate,
    OrderLine[] Lines,
    Address Address,
    string CardLast4,
    string? Description)
{
    public decimal Total => Lines.Sum(line => line.UnitPrice * line.Units);

    public OrderSummary ToSummary() => new(OrderId, Version, Status, Total, OrderDate, Description);
}

[GenerateSerializer, Immutable]
public sealed record StockLine(int ProductId, StockLineStatus Status);

[GenerateSerializer, Immutable]
public sealed record OrderDetails(OrderState Order, StockLine[] Stock);

// Messages.
[GenerateSerializer, Immutable]
public sealed record SubmitOrder(string BuyerId, OrderLine[] Lines, Address Address, string CardLast4);

// The order sends this to itself, scheduled for the end of the grace period.
[GenerateSerializer, Immutable]
public sealed record GracePeriodElapsed;

[GenerateSerializer, Immutable]
public sealed record OrderSummary(
    Guid OrderId,
    int Version,
    OrderStatus Status,
    decimal Total,
    DateTimeOffset OrderDate,
    string? Description);

[GenerateSerializer, Immutable]
public sealed record ReserveStock(Guid OrderId, int Units, GrainId ReplyTo);

[GenerateSerializer, Immutable]
public sealed record StockResult(int ProductId, bool Confirmed);

[GenerateSerializer, Immutable]
public sealed record ReleaseStock(Guid OrderId);

[GenerateSerializer, Immutable]
public sealed record CommitStock(Guid OrderId);

[GenerateSerializer, Immutable]
public sealed record AuthorizePayment(decimal Amount, string CardLast4, GrainId ReplyTo);

[GenerateSerializer, Immutable]
public sealed record PaymentResult(bool Succeeded, string? Reason);

public sealed class OrderingOptions
{
    // eShop's GracePeriodManager waits one minute; a shorter default keeps the demo responsive.
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromSeconds(10);
}

// Journaled state is stored as JSON lines, so each persisted type needs source-generated metadata.
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(StockLineStatus))]
[JsonSerializable(typeof(CatalogItem))]
[JsonSerializable(typeof(BasketItem))]
[JsonSerializable(typeof(OrderState))]
[JsonSerializable(typeof(OrderSummary))]
internal sealed partial class OrderProcessingJsonContext : JsonSerializerContext;
