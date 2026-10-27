using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Http;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.DurableTasks;

// <durable_tasks_payment_provider>
[GenerateSerializer, Immutable]
public sealed record ChargeResult(string ChargeId, decimal Amount);

// Stands in for a payment provider's client, such as Stripe's, which deduplicates requests by an idempotency key.
public interface IPaymentProvider
{
    Task<ChargeResult> ChargeAsync(string customerId, decimal amount, string idempotencyKey, CancellationToken cancellationToken);
}

// The provider rejected the charge, for example because the card was declined. Retrying returns the same rejection.
public sealed class PaymentDeclinedException(string reason) : Exception(reason);

// The provider couldn't process the request now, for example because of a rate limit or an outage.
public sealed class PaymentUnavailableException(TimeSpan? retryAfter) : Exception("The payment provider is unavailable.")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
// </durable_tasks_payment_provider>

// <durable_tasks_charge_outcome>
// A step returns the outcomes which the workflow handles as values, so a replay observes the same outcome.
[GenerateSerializer, Immutable]
public sealed record ChargeOutcome(ChargeResult? Charge, string? DeclineReason, TimeSpan? RetryAfter)
{
    public static ChargeOutcome Charged(ChargeResult charge) => new(charge, null, null);

    public static ChargeOutcome Declined(string reason) => new(null, reason, null);

    public static ChargeOutcome Unavailable(TimeSpan? retryAfter) => new(null, null, retryAfter ?? TimeSpan.FromMinutes(1));
}
// </durable_tasks_charge_outcome>

public interface ICheckoutGrain : IGrainWithGuidKey
{
    DurableTask<ChargeResult?> ChargeAsync(string customerId, decimal amount);

    DurableTask<ChargeResult?> ChargeWithBackoffAsync(string customerId, decimal amount);
}

public sealed class CheckoutGrain(IPaymentProvider payments) : DurableGrain, ICheckoutGrain
{
    // <durable_tasks_idempotency_key>
    // The grain, the task's ID, and its incarnation identify this run in every replay and every retry. The operation's
    // name distinguishes the run's external requests from each other.
    private string IdempotencyKey(string operation) =>
        $"{this.GetGrainId()}/{DurableTask.Current.Id}/{DurableTask.Current.Incarnation:N}/{operation}";
    // </durable_tasks_idempotency_key>

    // <durable_tasks_charge_in_step>
    public async DurableTask<ChargeResult?> ChargeAsync(string customerId, decimal amount)
    {
        var key = IdempotencyKey("charge");
        var outcome = await DurableTask.Run(async cancellationToken =>
        {
            // Retries brief, transient failures inside the step. Each attempt sends the same key, so the provider
            // charges once however many attempts reach it, including attempts which a replay of the step repeats.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return ChargeOutcome.Charged(await payments.ChargeAsync(customerId, amount, key, cancellationToken));
                }
                catch (PaymentDeclinedException declined)
                {
                    return ChargeOutcome.Declined(declined.Message);
                }
                catch (PaymentUnavailableException unavailable) when (attempt < 3)
                {
                    await Task.Delay(unavailable.RetryAfter ?? TimeSpan.FromSeconds(attempt), cancellationToken);
                }
                catch (PaymentUnavailableException unavailable)
                {
                    return ChargeOutcome.Unavailable(unavailable.RetryAfter);
                }
            }
        }).WithId("charge");

        return outcome.Charge;
    }
    // </durable_tasks_charge_in_step>

    // <durable_tasks_charge_with_backoff>
    public async DurableTask<ChargeResult?> ChargeWithBackoffAsync(string customerId, decimal amount)
    {
        var key = IdempotencyKey("charge");
        var deadline = DurableTask.Current.UtcNow.AddHours(6);
        while (true)
        {
            // Each attempt is a step, "charge", "charge$2", and so on, and each sends the same key.
            var outcome = await DurableTask.Run(async cancellationToken =>
            {
                try
                {
                    return ChargeOutcome.Charged(await payments.ChargeAsync(customerId, amount, key, cancellationToken));
                }
                catch (PaymentDeclinedException declined)
                {
                    return ChargeOutcome.Declined(declined.Message);
                }
                catch (PaymentUnavailableException unavailable)
                {
                    return ChargeOutcome.Unavailable(unavailable.RetryAfter);
                }
            }).WithId("charge");

            if (outcome.RetryAfter is not { } retryAfter)
            {
                // Charged or declined: a declined card stays declined, so the workflow doesn't retry it.
                return outcome.Charge;
            }

            if (DurableTask.Current.UtcNow + retryAfter > deadline)
            {
                throw new TimeoutException("The payment provider stayed unavailable for six hours.");
            }

            // A durable delay: the grain can deactivate while it waits, and the workflow resumes when the delay is due.
            await DurableTask.Delay(retryAfter).WithId("charge-backoff");
        }
    }
    // </durable_tasks_charge_with_backoff>
}

// <durable_tasks_database>
[GenerateSerializer, Immutable]
public sealed record ShipmentRecord(Guid OrderId, string Carrier, string TrackingNumber);

// Stands in for a data access layer over a relational database.
public interface IShipmentStore
{
    // INSERT ... ON CONFLICT (operation_id) DO NOTHING: a repeated write with the same operation ID changes nothing.
    Task UpsertAsync(string operationId, ShipmentRecord shipment, CancellationToken cancellationToken);

    Task<ShipmentRecord?> FindAsync(Guid orderId, CancellationToken cancellationToken);
}

public interface IShipmentGrain : IGrainWithGuidKey
{
    DurableTask<ShipmentRecord> RecordShipmentAsync(string carrier, string trackingNumber);
}

public sealed class ShipmentGrain(IShipmentStore store) : DurableGrain, IShipmentGrain
{
    public async DurableTask<ShipmentRecord> RecordShipmentAsync(string carrier, string trackingNumber)
    {
        var orderId = this.GetPrimaryKey();

        // A read in a step: the workflow decides from the recorded row, so a replay takes the same path.
        var existing = await DurableTask.Run(ct => store.FindAsync(orderId, ct)).WithId("find");
        if (existing is not null)
        {
            return existing;
        }

        // A write keyed by an ID which the task derives, so a write which a replay repeats changes nothing.
        var shipment = new ShipmentRecord(orderId, carrier, trackingNumber);
        var operationId = $"{this.GetGrainId()}/{DurableTask.Current.Id}/{DurableTask.Current.Incarnation:N}/insert";
        await DurableTask.Run(ct => store.UpsertAsync(operationId, shipment, ct)).WithId("insert");
        return shipment;
    }
}
// </durable_tasks_database>

// <durable_tasks_outbox_relay>
[GenerateSerializer, Immutable]
public sealed record ShipmentRequested(Guid OrderId, string Carrier);

public interface IShipmentRequestGrain : IGrainWithGuidKey
{
    DurableTask<bool> HandleAsync(ShipmentRequested request);
}

// A row which an application committed to its database's outbox table in the same transaction as its own change.
public sealed record OutboxRow(long Id, ShipmentRequested Request);

public interface IOutboxTable
{
    Task<IReadOnlyList<OutboxRow>> ReadPendingAsync(int count, CancellationToken cancellationToken);

    Task MarkSentAsync(long id, CancellationToken cancellationToken);
}

public sealed class OutboxRelay(IClusterClient client, IOutboxTable outbox)
{
    public async Task RelayAsync(CancellationToken cancellationToken)
    {
        foreach (var row in await outbox.ReadPendingAsync(100, cancellationToken))
        {
            // The row's ID names the task, so a relay which crashes before MarkSentAsync and sends the row again
            // reaches the same task, which runs once.
            await client.GetGrain<IShipmentRequestGrain>(row.Request.OrderId)
                .HandleAsync(row.Request)
                .WithId($"outbox-{row.Id}")
                .ScheduleAsync(cancellationToken);
            await outbox.MarkSentAsync(row.Id, cancellationToken);
        }
    }
}
// </durable_tasks_outbox_relay>

// <durable_tasks_http>
[GenerateSerializer, Immutable]
public sealed record LabelResult(string Url);

public interface ILabelPrinterGrain : IGrainWithGuidKey
{
    DurableTask<string> PrintLabelAsync(string address);
}

public sealed class LabelPrinterGrain([FromKeyedServices("labels")] IDurableHttpClient labels) : DurableGrain, ILabelPrinterGrain
{
    public async DurableTask<string> PrintLabelAsync(string address)
    {
        // A step: its request carries an Idempotency-Key derived from this step's identity, so a retry or a replay of
        // the step reaches the job which its first attempt started.
        var job = await labels.StartOperationAsync(() => new HttpRequestMessage(HttpMethod.Post, "labels")
        {
            Content = JsonContent.Create(new { address }),
        });

        try
        {
            // Reads the job's status events within a step, which records the result, and gives up after a day of
            // logical time.
            var label = await labels.WaitForOperationAsync<LabelResult>(job, new() { Mode = DurableHttpWaitMode.Events })
                .WaitAsync(TimeSpan.FromDays(1));
            return label.Url;
        }
        catch (Exception exception) when (exception is TimeoutException || DurableTask.Current.IsTaskCancellationRequested && exception is OperationCanceledException)
        {
            // Cancels the remote job, then lets this task end timed out or canceled.
            if (job.Location is { } location)
            {
                await labels.CancelOperationAsync(location, DurableTask.Current.TaskCancellationReason ?? "The label wasn't printed within a day.").Shielded();
            }

            throw;
        }
    }
}

internal static class LabelPrinterConfiguration
{
    internal static void ConfigureSilo(ISiloBuilder siloBuilder, Uri labelService)
    {
        // The handlers which the builder adds, such as resilience handlers, follow the key handler, so their retries
        // carry the key.
        siloBuilder.Services.AddDurableHttpClient("labels")
            .ConfigureHttpClient(client => client.BaseAddress = labelService);
    }
}
// </durable_tasks_http>

// <durable_tasks_http_operations>
public interface IReturnGrain : IGrainWithStringKey
{
    DurableTask<string> ProcessReturnAsync(string reason);
}

internal static class ReturnEndpoints
{
    public static readonly DurableSignal<bool> Inspected = new("inspected");

    internal static void Map(WebApplicationBuilder builder)
    {
        builder.Services.AddDurableHttp();
        var app = builder.Build();

        // Each POST starts the grain call as an operation which the request's Idempotency-Key names, within the caller's
        // scope, and the group serves its status monitor, events, cancellation, and signals under
        // /returns/{returnId}/operations/{id}. The scope "returns" names the operations, so renaming the routes keeps them.
        var returns = app.MapGroup("/returns/{returnId}")
            .WithDurableOperations("returns")
            .RequireAuthorization("returns");

        returns.MapPost("/process", (string returnId, string reason, IGrainFactory grains) =>
            grains.GetGrain<IReturnGrain>(returnId).ProcessReturnAsync(reason));
        returns.MapDurableSignal(Inspected);
    }
}
// </durable_tasks_http_operations>

// <durable_tasks_webhook>
[GenerateSerializer, Immutable]
public sealed record PaymentConfirmation(string EventId, string ChargeId, bool Succeeded);

public interface IPaymentWorkflowGrain : IGrainWithGuidKey
{
    DurableTask<bool> AwaitPaymentAsync(string chargeId);

    Task<DurableSignalResult> OnPaymentWebhookAsync(PaymentConfirmation confirmation);
}

public sealed class PaymentWorkflowGrain(IDurableTaskManager tasks) : DurableGrain, IPaymentWorkflowGrain
{
    private static readonly DurableSignal<PaymentConfirmation> Confirmed = new("payment-confirmed");

    public static string WorkflowId(Guid paymentId) => $"payment-{paymentId:N}";

    public async DurableTask<bool> AwaitPaymentAsync(string chargeId)
    {
        // A webhook which arrives before the wait is recorded, and the wait returns it at once.
        var (received, confirmation) = await Confirmed.TryWaitAsync(DurableTask.Current.UtcNow.AddDays(1)).WithId("confirmation");
        return received && confirmation.Succeeded;
    }

    // Called by the web endpoint which verified the webhook's signature. A provider which delivers the same event again
    // receives Duplicate, which the endpoint acknowledges like Accepted.
    public async Task<DurableSignalResult> OnPaymentWebhookAsync(PaymentConfirmation confirmation) =>
        await tasks.TrySignalAsync(WorkflowId(this.GetPrimaryKey()), Confirmed, confirmation);
}
// </durable_tasks_webhook>

// <durable_tasks_rejections>
public interface IFollowUpGrain : IGrainWithGuidKey
{
    DurableTask<string> ScheduleFollowUpAsync(Guid orderId);
}

public sealed class FollowUpGrain : DurableGrain, IFollowUpGrain
{
    public async DurableTask<string> ScheduleFollowUpAsync(Guid orderId)
    {
        try
        {
            // Messaging retries delivery of the start until the target accepts it, so the workflow doesn't retry it.
            var survey = await GrainFactory.GetGrain<IExpenseGrain>(orderId)
                .SubmitAsync(new ExpenseClaim("survey", 0))
                .Detached($"survey-{orderId:N}")
                .ScheduleAsync();
            return survey.TargetTaskId.ToString();
        }
        catch (DurableTaskConflictException)
        {
            // The ID already names another request, and every retry reaches the same conflict.
            return "conflict";
        }
        catch (DurableTaskDeliveryException)
        {
            // The start wasn't accepted within Messaging's OutboxRetryTimeout.
            return "undelivered";
        }
    }
}
// </durable_tasks_rejections>

// <durable_tasks_deduplicating_receiver>
public interface ILedgerGrain : IGrainWithStringKey
{
    Task<decimal> CreditAsync(string operationId, decimal amount);
}

public sealed class LedgerGrain : DurableGrain, ILedgerGrain
{
    private readonly IDurableValue<decimal> _balance;
    private readonly IDurableDictionary<string, decimal> _applied;

    public LedgerGrain()
    {
        _balance = StateManager.GetOrAddValue<decimal>("balance");
        _applied = StateManager.GetOrAddDictionary<string, decimal>("applied");
    }

    // An ordinary method which callers outside durable tasks retry: the operation ID and the credit commit together,
    // so a retry finds the operation and returns the balance which it produced.
    public async Task<decimal> CreditAsync(string operationId, decimal amount)
    {
        if (_applied.TryGetValue(operationId, out var balance))
        {
            return balance;
        }

        _balance.Value += amount;
        _applied[operationId] = _balance.Value;
        await WriteStateAsync();
        return _balance.Value;
    }
}
// </durable_tasks_deduplicating_receiver>
