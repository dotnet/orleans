namespace Documentation.Diagnostics.DurableTasks;

// Declarations which the examples share. The external services stand in for clients of systems outside Orleans.

public enum OrderStatus
{
    Submitted,
    Reserved,
    Paid,
    Shipped,
    Cancelled,
}

[GenerateSerializer, Immutable]
public sealed record Charge(string ChargeId, decimal Amount);

[GenerateSerializer, Immutable]
public sealed record Decision(string ApproverId, bool Approved);

[GenerateSerializer, Immutable]
public sealed record OrderResult(OrderStatus Status, string? Reason);

public interface IPaymentGateway
{
    Task<Charge> ChargeAsync(string idempotencyKey, decimal amount, CancellationToken cancellationToken);
}

public interface IExchangeRates
{
    Task<decimal> GetRateAsync(string currency, CancellationToken cancellationToken);
}

public interface IEmailSender
{
    Task SendAsync(string recipient, string subject, CancellationToken cancellationToken);
}

public interface IDocumentHasher
{
    Task<string> HashAsync(byte[] document, CancellationToken cancellationToken);
}

// Stands in for a grain interface whose durable methods return DurableTask, such as an order grain's.
public interface IOrderWorkflows
{
    DurableTask<OrderResult> PlaceOrderAsync(Guid orderId);
}
