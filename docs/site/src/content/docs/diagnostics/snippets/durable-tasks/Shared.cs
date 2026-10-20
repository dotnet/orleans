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

public interface IRoomInventory
{
    Task ReleaseAsync(string room, CancellationToken cancellationToken);
}

// Stands in for a grain interface whose durable methods return DurableTask, such as an order grain's.
public interface IOrderWorkflows
{
    DurableTask<OrderResult> PlaceOrderAsync(Guid orderId);
}

// Grains whose durable methods other examples call, so a detached start runs the call as a root task on its grain.
public interface ICustomerSurveys : IGrainWithStringKey
{
    DurableTask SendSurveyAsync(string customer);
}

public sealed class CustomerSurveysGrain(IEmailSender email) : DurableGrain, ICustomerSurveys
{
    public async DurableTask SendSurveyAsync(string customer) =>
        await email.SendAsync(customer, "How was your order?", DurableTask.Current.CancellationToken);
}

public interface IStudentMail : IGrainWithStringKey
{
    DurableTask<bool> WelcomeAsync(string student, int seat);
}

public sealed class StudentMailGrain(IEmailSender email) : DurableGrain, IStudentMail
{
    public async DurableTask<bool> WelcomeAsync(string student, int seat)
    {
        await email.SendAsync(student, $"Welcome! Your seat is {seat}.", DurableTask.Current.CancellationToken);
        return true;
    }
}