using System.Text.Json.Serialization;

namespace DurableOrderProcessing;

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

// A snapshot of an order: the entry which each step of the order's workflow reports to its history. Each transition
// returns the next snapshot as a pure function of the current one and its inputs, and advances the order's version.
[GenerateSerializer, Immutable]
public sealed record OrderDetails(OrderState Order, StockLine[] Stock)
{
    public static OrderDetails Submit(Guid orderId, SubmitOrder request, DateTimeOffset orderDate) => new(
        new OrderState(
            orderId,
            request.BuyerId,
            Version: 0,
            OrderStatus.Submitted,
            orderDate,
            request.Lines,
            request.Address,
            request.CardLast4,
            Description: null),
        []);

    public OrderDetails BeginValidation() =>
        Next(OrderStatus.AwaitingValidation, [.. Order.Lines.Select(line => new StockLine(line.ProductId, StockLineStatus.Pending))]);

    // Records each line's reservation. The order confirms its stock when every line was reserved; otherwise its status
    // stays unchanged until the workflow cancels it.
    public OrderDetails RecordStock(bool[] confirmed)
    {
        StockLine[] stock = [.. Order.Lines.Select((line, i) =>
            new StockLine(line.ProductId, confirmed[i] ? StockLineStatus.Confirmed : StockLineStatus.Rejected))];
        return Array.IndexOf(confirmed, false) < 0 ? Next(OrderStatus.StockConfirmed, stock) : this with { Stock = stock };
    }

    public OrderDetails MarkPaid() =>
        Next(OrderStatus.Paid, [.. Stock.Select(line => line with { Status = StockLineStatus.Committed })]);

    public OrderDetails MarkShipped() => Next(OrderStatus.Shipped, Stock);

    public OrderDetails Cancel(int[] released, string? reason) => Next(
        OrderStatus.Cancelled,
        [.. Stock.Select(line => released.Contains(line.ProductId) ? line with { Status = StockLineStatus.Released } : line)],
        reason);

    private OrderDetails Next(OrderStatus status, StockLine[] stock, string? description = null) =>
        new(Order with { Status = status, Version = Order.Version + 1, Description = description }, stock);
}

// Durable task arguments and results. The runtime stores them with the Orleans serializer.
[GenerateSerializer, Immutable]
public sealed record SubmitOrder(string BuyerId, OrderLine[] Lines, Address Address, string CardLast4);

[GenerateSerializer, Immutable]
public sealed record OrderSummary(
    Guid OrderId,
    int Version,
    OrderStatus Status,
    decimal Total,
    DateTimeOffset OrderDate,
    string? Description);

[GenerateSerializer, Immutable]
public sealed record PaymentResult(bool Succeeded, string? Reason);

// The outcome of an order's PlaceOrder workflow.
[GenerateSerializer, Immutable]
public sealed record OrderResult(OrderStatus Status, string? Description);

// The value of an order's Shipped signal. The workflow records the first shipment; another one conflicts with it.
[GenerateSerializer, Immutable]
public sealed record Shipment(DateTimeOffset ShippedAt);

public sealed class OrderingOptions
{
    // eShop's GracePeriodManager waits one minute; a shorter default keeps the demo responsive.
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromSeconds(10);
}

// Journaled state is stored as JSON lines, so each persisted type needs source-generated metadata.
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(CatalogItem))]
[JsonSerializable(typeof(BasketItem))]
[JsonSerializable(typeof(OrderDetails))]
[JsonSerializable(typeof(StockLine))]
[JsonSerializable(typeof(OrderSummary))]
internal sealed partial class DurableOrderProcessingJsonContext : JsonSerializerContext;
