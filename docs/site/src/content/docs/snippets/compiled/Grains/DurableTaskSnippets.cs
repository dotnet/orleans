using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;

#pragma warning disable ORLEANSEXP005

namespace Documentation.Grains.DurableTasks;

internal static class DurableTaskConfiguration
{
    internal static void ConfigureSilo(ISiloBuilder siloBuilder)
    {
        // <durable_tasks_registration>
        siloBuilder.UseInMemoryDurableJobs();
        siloBuilder.AddVolatileJournalStorage();
        siloBuilder.AddDurableTasks(options => options.ResultRetentionPeriod = TimeSpan.FromDays(7));
        // </durable_tasks_registration>
    }

    internal static void ConfigureClient(IClientBuilder clientBuilder)
    {
        // <durable_tasks_client_registration>
        clientBuilder.AddDurableTasks();
        // </durable_tasks_client_registration>
    }
}

// <durable_tasks_contracts>
public enum OrderStatus
{
    None,
    Submitted,
    AwaitingValidation,
    Paid,
    Shipped,
    Cancelled,
}

[GenerateSerializer, Immutable]
public sealed record OrderLine(int ProductId, decimal UnitPrice, int Units);

[GenerateSerializer, Immutable]
public sealed record SubmitOrder(string BuyerId, OrderLine[] Lines, string CardLast4);

[GenerateSerializer, Immutable]
public sealed record PaymentResult(bool Succeeded, string? Reason);

[GenerateSerializer, Immutable]
public sealed record OrderResult(OrderStatus Status, string? Description);

[GenerateSerializer, Immutable]
public sealed record Shipment(string Carrier, string TrackingNumber);

public interface ICatalogItemGrain : IGrainWithIntegerKey
{
    Task RestockAsync(int units);

    DurableTask<bool> ReserveAsync(Guid orderId, int units);

    // Returns whether the order held a reservation, so releasing is idempotent.
    DurableTask<bool> ReleaseAsync(Guid orderId);
}

public interface IPaymentGrain : IGrainWithGuidKey
{
    DurableTask<PaymentResult> AuthorizeAsync(decimal amount, string cardLast4);
}

public interface IOrderGrain : IGrainWithGuidKey
{
    DurableTask<OrderResult> PlaceOrderAsync(SubmitOrder order);

    Task<bool> CancelAsync();

    Task AbortAsync(string reason);

    Task<bool> ShipAsync(Shipment shipment);
}
// </durable_tasks_contracts>

// <durable_tasks_catalog_grain>
public sealed class CatalogItemGrain : DurableGrain, ICatalogItemGrain
{
    private readonly IDurableValue<int> _available;
    private readonly IDurableDictionary<Guid, int> _reservations;

    public CatalogItemGrain()
    {
        _available = StateManager.GetOrAddValue<int>("available");
        _reservations = StateManager.GetOrAddDictionary<Guid, int>("reservations");
    }

    public async Task RestockAsync(int units)
    {
        _available.Value += units;
        await WriteStateAsync();
    }

    // The state change and the task's result commit in one journal write.
    public async DurableTask<bool> ReserveAsync(Guid orderId, int units)
    {
        if (units <= 0 || _available.Value < units)
        {
            return false;
        }

        _available.Value -= units;
        _reservations[orderId] = units;
        return true;
    }

    public async DurableTask<bool> ReleaseAsync(Guid orderId)
    {
        if (!_reservations.Remove(orderId, out var units))
        {
            return false;
        }

        _available.Value += units;
        return true;
    }
}
// </durable_tasks_catalog_grain>

public sealed class PaymentGrain : DurableGrain, IPaymentGrain
{
    public async DurableTask<PaymentResult> AuthorizeAsync(decimal amount, string cardLast4) =>
        cardLast4 == "0000" ? new PaymentResult(false, "The card was declined.") : new PaymentResult(true, null);
}

// <durable_tasks_order_grain>
public sealed class OrderGrain : DurableGrain, IOrderGrain
{
    private static readonly DurableSignal<Shipment> Shipped = new("shipped");

    private readonly IDurableTaskManager _tasks;
    private readonly IDurableValue<OrderStatus> _status;

    public OrderGrain(IDurableTaskManager tasks)
    {
        _tasks = tasks;
        _status = StateManager.GetOrAddValue<OrderStatus>("status");
    }

    private Guid OrderId => this.GetPrimaryKey();

    public static string PlaceOrderTaskId(Guid orderId) => $"place-order-{orderId:N}";

    public async DurableTask<OrderResult> PlaceOrderAsync(SubmitOrder order)
    {
        DurableTask.Once("submit", () => SetStatus(OrderStatus.Submitted));

        // <durable_tasks_fan_out>
        bool[] reserved;
        try
        {
            await DurableTask.Delay(TimeSpan.FromMinutes(1)).WithId("grace-period");
            DurableTask.Once("validate", () => SetStatus(OrderStatus.AwaitingValidation));

            // Reserves every line concurrently, as the children reserve/0, reserve/1, and so on.
            reserved = await DurableTask.WhenAll(
                    order.Lines.Select(line => Catalog(line.ProductId).ReserveAsync(OrderId, line.Units)))
                .WithId("reserve");
        }
        catch (OperationCanceledException) when (DurableTask.Current.IsCancellationRequested)
        {
            // WhenAll has waited for every reservation, so none is still running.
            return await CancelOrderAsync(order.Lines, DurableTask.Current.CancellationReason);
        }

        if (Array.IndexOf(reserved, false) >= 0)
        {
            return await CancelOrderAsync(order.Lines, "A line is out of stock.");
        }
        // </durable_tasks_fan_out>

        var payment = await GrainFactory.GetGrain<IPaymentGrain>(OrderId)
            .AuthorizeAsync(order.Lines.Sum(line => line.UnitPrice * line.Units), order.CardLast4)
            .WithId("authorize-payment");
        if (!payment.Succeeded)
        {
            return await CancelOrderAsync(order.Lines, payment.Reason);
        }

        DurableTask.Once("pay", () => SetStatus(OrderStatus.Paid));

        // Returns at once when the shipment arrived before the wait.
        var shipment = await Shipped.WaitAsync();
        DurableTask.Once("ship", () => SetStatus(OrderStatus.Shipped));
        return new OrderResult(OrderStatus.Shipped, shipment.TrackingNumber);
    }

    // <durable_tasks_order_controls>
    public async Task<bool> CancelAsync()
    {
        if (_status.Value is not (OrderStatus.Submitted or OrderStatus.AwaitingValidation))
        {
            return false;
        }

        await _tasks.CancelAsync(PlaceOrderTaskId(OrderId), "Cancelled by the buyer.");
        return true;
    }

    public async Task AbortAsync(string reason) => await _tasks.AbortAsync(PlaceOrderTaskId(OrderId), reason);

    public async Task<bool> ShipAsync(Shipment shipment)
    {
        if (_status.Value is not OrderStatus.Paid)
        {
            return false;
        }

        var result = await _tasks.TrySignalAsync(PlaceOrderTaskId(OrderId), Shipped, shipment);
        return result is DurableSignalResult.Accepted or DurableSignalResult.Duplicate;
    }
    // </durable_tasks_order_controls>

    // <durable_tasks_compensation>
    private async Task<OrderResult> CancelOrderAsync(OrderLine[] lines, string? reason)
    {
        // The release is shielded, so it runs to completion after the cancellation which ended the workflow.
        await DurableTask.WhenAll(lines.Select(line => Catalog(line.ProductId).ReleaseAsync(OrderId)))
            .WithId("release")
            .Shielded();
        DurableTask.Once("cancel", () => SetStatus(OrderStatus.Cancelled));
        return new OrderResult(OrderStatus.Cancelled, reason);
    }
    // </durable_tasks_compensation>

    // <durable_tasks_once>
    // The workflow runs each status change as a recorded effect, such as DurableTask.Once("pay", ...): the state change
    // commits with its recorded result, and a replay returns the result without changing the state again.
    private OrderStatus SetStatus(OrderStatus status)
    {
        _status.Value = status;
        return status;
    }
    // </durable_tasks_once>

    private ICatalogItemGrain Catalog(int productId) => GrainFactory.GetGrain<ICatalogItemGrain>(productId);
}
// </durable_tasks_order_grain>

// <durable_tasks_schedule_from_grain>
public interface IBasketGrain : IGrainWithStringKey
{
    Task AddAsync(OrderLine line);

    Task<Guid?> CheckoutAsync(Guid orderId, string cardLast4);

    Task<OrderResult?> GetOrderResultAsync(Guid orderId);
}

public sealed class BasketGrain : DurableGrain, IBasketGrain
{
    private readonly IDurableDictionary<int, OrderLine> _lines;

    public BasketGrain() => _lines = StateManager.GetOrAddDictionary<int, OrderLine>("lines");

    public async Task AddAsync(OrderLine line)
    {
        _lines[line.ProductId] = line;
        await WriteStateAsync();
    }

    public async Task<Guid?> CheckoutAsync(Guid orderId, string cardLast4)
    {
        if (_lines.Count == 0)
        {
            return null;
        }

        var order = new SubmitOrder(this.GetPrimaryKeyString(), [.. _lines.Values], cardLast4);
        _lines.Clear();

        // Posts the invocation to the outbox, and the journal write commits it with the cleared basket.
        _ = GrainFactory.GetGrain<IOrderGrain>(orderId)
            .PlaceOrderAsync(order)
            .WithId(OrderGrain.PlaceOrderTaskId(orderId))
            .Post();
        await WriteStateAsync();
        return orderId;
    }

    // Polls the workflow on the order grain, which retains its result.
    public async Task<OrderResult?> GetOrderResultAsync(Guid orderId)
    {
        try
        {
            var response = await GrainFactory.GetGrain<IOrderGrain>(orderId)
                .GetDurableTask<OrderResult>(OrderGrain.PlaceOrderTaskId(orderId))
                .GetResponseAsync(new PollingOptions { PollTimeout = TimeSpan.Zero });
            return response.Status is DurableTaskStatus.CompletedSuccessfully ? response.GetResult<OrderResult>() : null;
        }
        catch (DurableTaskNotFoundException)
        {
            // No checkout started this order, the checkout's invocation hasn't reached the order yet, or the result's
            // retention period has ended.
            return null;
        }
    }
}
// </durable_tasks_schedule_from_grain>

internal static class OrderClient
{
    // <durable_tasks_schedule_from_client>
    public static async Task<OrderResult> PlaceOrderAsync(IClusterClient client, Guid orderId, SubmitOrder order)
    {
        // Scheduling the same request under the same root ID again returns the same task, so a retry is safe.
        ScheduledTask<OrderResult> workflow = await client.GetGrain<IOrderGrain>(orderId)
            .PlaceOrderAsync(order)
            .WithId(OrderGrain.PlaceOrderTaskId(orderId))
            .ScheduleAsync();
        return await workflow;
    }

    public static async Task<DurableTaskStatus> GetStatusAsync(IClusterClient client, Guid orderId)
    {
        var workflow = client.GetGrain<IOrderGrain>(orderId)
            .GetDurableTask<OrderResult>(OrderGrain.PlaceOrderTaskId(orderId));
        return await workflow.GetStatusAsync(new PollingOptions { PollTimeout = TimeSpan.Zero });
    }
    // </durable_tasks_schedule_from_client>

    // <durable_tasks_client_controls>
    public static async Task CancelAsync(IClusterClient client, Guid orderId, string reason)
    {
        var workflow = client.GetGrain<IOrderGrain>(orderId)
            .GetDurableTask<OrderResult>(OrderGrain.PlaceOrderTaskId(orderId));
        await workflow.CancelAsync(reason);
    }

    public static async Task AbortAsync(IClusterClient client, Guid orderId, string reason)
    {
        var workflow = client.GetGrain<IOrderGrain>(orderId)
            .GetDurableTask<OrderResult>(OrderGrain.PlaceOrderTaskId(orderId));
        await workflow.AbortAsync(reason);
    }
    // </durable_tasks_client_controls>
}

// <durable_tasks_race>
[GenerateSerializer, Immutable]
public sealed record ShippingQuote(string Carrier, decimal Price);

public interface ICarrierGrain : IGrainWithStringKey
{
    // Returns null when the carrier doesn't serve the route.
    DurableTask<ShippingQuote?> QuoteAsync(string route);
}

public interface IShippingGrain : IGrainWithGuidKey
{
    DurableTask<ShippingQuote?> QuoteFirstAsync(string route);

    DurableTask<ShippingQuote?> QuoteGroundAsync(string route);

    DurableTask<ShippingQuote[]> CollectQuotesAsync(string route, string[] carriers, int count);
}

public sealed partial class ShippingGrain : DurableGrain, IShippingGrain
{
    // Returns the first carrier's quote once the other carrier's request has settled.
    public async DurableTask<ShippingQuote?> QuoteFirstAsync(string route)
    {
        var (_, quote) = await DurableTask.Race([Carrier("ground").QuoteAsync(route), Carrier("air").QuoteAsync(route)])
            .WithId("first-quote");
        return quote;
    }

    private ICarrierGrain Carrier(string name) => GrainFactory.GetGrain<ICarrierGrain>(name);
}
// </durable_tasks_race>

public sealed partial class ShippingGrain
{
    // <durable_tasks_timeout>
    public async DurableTask<ShippingQuote?> QuoteGroundAsync(string route)
    {
        try
        {
            return await Carrier("ground").QuoteAsync(route).WaitAsync(TimeSpan.FromMinutes(2)).WithId("ground-quote");
        }
        catch (TimeoutException)
        {
            // The carrier's request ended canceled after the timeout requested its cancellation.
            return null;
        }
    }
    // </durable_tasks_timeout>
}

public sealed partial class ShippingGrain
{
    // <durable_tasks_when_each>
    public async DurableTask<ShippingQuote[]> CollectQuotesAsync(string route, string[] carriers, int count)
    {
        var requests = await DurableTask.ScheduleAll(carriers.Select(carrier => Carrier(carrier).QuoteAsync(route)))
            .WithId("quotes");

        var quotes = new List<ShippingQuote>();
        await foreach (var request in ScheduledTask.WhenEach(requests))
        {
            if (request.TryGetResult(out var quote) && quote is not null)
            {
                quotes.Add(quote);
                if (quotes.Count == count)
                {
                    break;
                }
            }
        }

        // Canceling a request which already completed changes nothing.
        foreach (var request in requests)
        {
            await request.CancelAsync("Enough quotes were collected.");
        }

        return [.. quotes];
    }
    // </durable_tasks_when_each>
}

// <durable_tasks_when_all_completed>
public interface IAllocationGrain : IGrainWithGuidKey
{
    DurableTask<bool> AllocateAsync(OrderLine[] lines);
}

public sealed class AllocationGrain : DurableGrain, IAllocationGrain
{
    private Guid OrderId => this.GetPrimaryKey();

    public async DurableTask<bool> AllocateAsync(OrderLine[] lines)
    {
        var reservations = await DurableTask.WhenAllCompleted(
                lines.Select(line => Catalog(line.ProductId).ReserveAsync(OrderId, line.Units)))
            .WithId("reserve");
        if (reservations.All(reservation => reservation.TryGetResult(out var reserved) && reserved))
        {
            return true;
        }

        // Releases exactly the lines whose reservation succeeded, including after a cancellation request.
        var held = lines.Where((_, i) => reservations[i].TryGetResult(out var reserved) && reserved);
        await DurableTask.WhenAll(held.Select(line => Catalog(line.ProductId).ReleaseAsync(OrderId)))
            .WithId("release")
            .Shielded();

        DurableTask.Current.CancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private ICatalogItemGrain Catalog(int productId) => GrainFactory.GetGrain<ICatalogItemGrain>(productId);
}
// </durable_tasks_when_all_completed>

// <durable_tasks_schedule_all>
[GenerateSerializer, Immutable]
public sealed record Package(string PackageId, decimal WeightKg);

public interface ILabelGrain : IGrainWithStringKey
{
    DurableTask<string> PrintAsync(Package package);
}

public interface IFulfillmentGrain : IGrainWithGuidKey
{
    DurableTask<string[]> FulfillAsync(Package[] packages);
}

public sealed class FulfillmentGrain : DurableGrain, IFulfillmentGrain
{
    private readonly IDurableValue<bool> _picked;

    public FulfillmentGrain() => _picked = StateManager.GetOrAddValue<bool>("picked");

    public async DurableTask<string[]> FulfillAsync(Package[] packages)
    {
        // Starts printing every label, as the children labels/0, labels/1, and so on, without waiting for them.
        var labels = await DurableTask.ScheduleAll(
                packages.Select(package => GrainFactory.GetGrain<ILabelGrain>(package.PackageId).PrintAsync(package)))
            .WithId("labels");

        await MarkPicked().WithId("pick");

        // Throws the first failure in input order, otherwise the first cancellation.
        await ScheduledTask.WhenAll(labels);
        return [.. labels.Select(label => label.GetResult())];
    }

    private async DurableTask<bool> MarkPicked() => _picked.Value = true;
}
// </durable_tasks_schedule_all>

// <durable_tasks_signal>
[GenerateSerializer, Immutable]
public sealed record ExpenseClaim(string EmployeeId, decimal Amount);

[GenerateSerializer, Immutable]
public sealed record Decision(string ApproverId, bool Approved);

public enum ExpenseOutcome
{
    Approved,
    Rejected,
    Expired,
}

public interface IExpenseGrain : IGrainWithGuidKey
{
    DurableTask<ExpenseOutcome> SubmitAsync(ExpenseClaim claim);

    Task<DurableSignalResult> DecideAsync(Decision decision);
}

public sealed class ExpenseGrain(IDurableTaskManager tasks) : DurableGrain, IExpenseGrain
{
    public static readonly DurableSignal<Decision> Decided = new("decided");

    public static string SubmitTaskId(Guid expenseId) => $"expense-{expenseId:N}";

    public async DurableTask<ExpenseOutcome> SubmitAsync(ExpenseClaim claim)
    {
        // Logical time, which a replay observes unchanged.
        var deadline = DurableTask.Current.UtcNow.AddDays(3);
        var (received, decision) = await Decided.TryWaitAsync(deadline).WithId("decision");
        if (!received)
        {
            return ExpenseOutcome.Expired;
        }

        return decision.Approved ? ExpenseOutcome.Approved : ExpenseOutcome.Rejected;
    }

    public async Task<DurableSignalResult> DecideAsync(Decision decision) =>
        await tasks.TrySignalAsync(SubmitTaskId(this.GetPrimaryKey()), Decided, decision);
}
// </durable_tasks_signal>

internal static class ExpenseClient
{
    // <durable_tasks_signal_client>
    public static async Task<DurableSignalResult> DecideAsync(IClusterClient client, Guid expenseId, Decision decision) =>
        await client.GetGrain<IExpenseGrain>(expenseId).TrySignalDurableTaskAsync(
            ExpenseGrain.SubmitTaskId(expenseId),
            ExpenseGrain.Decided,
            decision);
    // </durable_tasks_signal_client>
}

// <durable_tasks_detach>
[GenerateSerializer, Immutable]
public sealed record InvoiceRequest(string CustomerId, decimal Amount);

public interface INotificationGrain : IGrainWithStringKey
{
    // Returns whether this call sent the receipt, so a repeated receipt is a no-op.
    DurableTask<bool> SendReceiptAsync(Guid invoiceId, decimal amount);
}

public interface IInvoiceGrain : IGrainWithGuidKey
{
    DurableTask<decimal> IssueAsync(InvoiceRequest request);
}

public sealed class InvoiceGrain : DurableGrain, IInvoiceGrain
{
    private readonly IDurableValue<decimal> _issued;

    public InvoiceGrain() => _issued = StateManager.GetOrAddValue<decimal>("issued");

    public static string ReceiptTaskId(Guid invoiceId) => $"receipt-{invoiceId:N}";

    public async DurableTask<decimal> IssueAsync(InvoiceRequest request)
    {
        var amount = await Record(request.Amount).WithId("record");

        // Starts the root task "receipt-{invoiceId}" on the notification grain, which commits with the invoice's result.
        _ = GrainFactory.GetGrain<INotificationGrain>(request.CustomerId)
            .SendReceiptAsync(this.GetPrimaryKey(), amount)
            .Detached()
            .WithId(ReceiptTaskId(this.GetPrimaryKey()))
            .Post();
        return amount;
    }

    private async DurableTask<decimal> Record(decimal amount) => _issued.Value = amount;
}
// </durable_tasks_detach>

// <durable_tasks_replay_regions>
[GenerateSerializer, Immutable]
public sealed record Ticket(int Number, Guid Reference, DateTimeOffset IssuedAt);

public interface ITicketGrain : IGrainWithStringKey
{
    DurableTask<Ticket> IssueAsync(decimal amount, string cardLast4);
}

public sealed class TicketGrain : DurableGrain, ITicketGrain
{
    private readonly ILogger _logger;
    private readonly IDurableValue<int> _lastNumber;
    private readonly IDurableDictionary<int, bool> _paid;

    public TicketGrain(ILogger<TicketGrain> logger)
    {
        // Skips events while a task replays, so each is logged once.
        _logger = logger.ReplaySafe();
        _lastNumber = StateManager.GetOrAddValue<int>("last-number");
        _paid = StateManager.GetOrAddDictionary<int, bool>("paid");
    }

    public async DurableTask<Ticket> IssueAsync(decimal amount, string cardLast4)
    {
        // Re-executed code: it runs in every replay, and logs once through the replay-safe logger.
        _logger.LogInformation("Issuing a ticket for {Amount}.", amount);

        // A recorded effect advances the counter, draws a reference, and reads the clock once. Its change to the counter
        // commits with its recorded ticket, and a replay returns that ticket without running the effect again.
        var ticket = DurableTask.Once(
            "allocate",
            () => new Ticket(++_lastNumber.Value, Guid.NewGuid(), DateTimeOffset.UtcNow));

        var payment = await GrainFactory.GetGrain<IPaymentGrain>(ticket.Reference)
            .AuthorizeAsync(amount, cardLast4)
            .WithId("authorize");

        // The synchronous tail: no durable operation follows this change, so it commits with the task's result.
        _paid[ticket.Number] = payment.Succeeded;
        return ticket;
    }
}
// </durable_tasks_replay_regions>

// <durable_tasks_progress>
[GenerateSerializer, Immutable]
public sealed record ExportProgress(int Completed, int Total);

public interface IPartitionGrain : IGrainWithStringKey
{
    DurableTask ExportAsync();
}

public interface IExportGrain : IGrainWithStringKey
{
    DurableTask<int> ExportAsync(string[] partitions);

    IAsyncEnumerable<ExportProgress> WatchAsync(CancellationToken cancellationToken);
}

public sealed class ExportGrain : DurableGrain, IExportGrain
{
    private readonly IDurableLog<ExportProgress> _progress;

    public ExportGrain() => _progress = StateManager.GetOrAddLog<ExportProgress>("progress");

    public async DurableTask<int> ExportAsync(string[] partitions)
    {
        // Each append runs once for its position in the task, so a replay doesn't publish older progress again.
        DurableTask.Once(() => _progress.Append(new ExportProgress(0, partitions.Length)));
        for (var i = 0; i < partitions.Length; i++)
        {
            await GrainFactory.GetGrain<IPartitionGrain>(partitions[i]).ExportAsync().WithId("partition");
            DurableTask.Once(() => _progress.Append(new ExportProgress(i + 1, partitions.Length)));
        }

        return partitions.Length;
    }

    // Observers read committed entries in order, from the first.
    public async IAsyncEnumerable<ExportProgress> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var change in _progress.ReadAsync(cancellationToken: cancellationToken))
        {
            if (change is { Kind: LogEventKind.Entry, Item: { } progress })
            {
                yield return progress;
                if (progress.Completed == progress.Total)
                {
                    yield break;
                }
            }
        }
    }
}
// </durable_tasks_progress>

// <durable_tasks_stream_draft>
public interface IChatModel
{
    IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken cancellationToken);
}

public interface IConversationGrain : IGrainWithStringKey
{
    DurableTask<long> AskAsync(string prompt);

    IAsyncEnumerable<LogEvent<string>> WatchAsync(long fromSequence, CancellationToken cancellationToken);
}

public sealed class ConversationGrain : DurableGrain, IConversationGrain
{
    private readonly IChatModel _model;
    private readonly IDurableLog<string> _transcript;

    public ConversationGrain(IChatModel model)
    {
        _model = model;
        _transcript = StateManager.GetOrAddLog<string>("transcript");
    }

    // Returns the sequence number of the committed response.
    public async DurableTask<long> AskAsync(string prompt) => await DurableTask.Run(async cancellationToken =>
    {
        using var draft = _transcript.BeginDraft();
        var response = new StringBuilder();
        await foreach (var chunk in _model.StreamAsync(prompt, cancellationToken))
        {
            // Volatile: watchers receive each chunk as it arrives, and no journal write includes it.
            draft.Write(chunk);
            response.Append(chunk);
        }

        // The step's final synchronous code: the entry commits in the same journal write as the step's result.
        return draft.Commit(response.ToString());
    }).WithId("respond");

    // Watchers receive the draft's chunks live, then the committed entry which replaces them.
    public IAsyncEnumerable<LogEvent<string>> WatchAsync(long fromSequence, CancellationToken cancellationToken) =>
        _transcript.ReadAsync(fromSequence, DurableWatchMode.Drafts, cancellationToken: cancellationToken);
}
// </durable_tasks_stream_draft>

internal static class InvoiceClient
{
    // <durable_tasks_detach_observe>
    public static async Task<DurableTaskStatus> GetReceiptStatusAsync(IClusterClient client, Guid invoiceId, string customerId)
    {
        var receipt = client.GetGrain<INotificationGrain>(customerId).GetDurableTask<bool>(InvoiceGrain.ReceiptTaskId(invoiceId));
        return await receipt.GetStatusAsync(new PollingOptions { PollTimeout = TimeSpan.Zero });
    }
    // </durable_tasks_detach_observe>
}

internal static class DurableTaskDiagnostics
{
    // <durable_tasks_diagnostics>
    public static async Task PrintTasksAsync(IGrainFactory grains, Guid orderId)
    {
        var runtime = grains.GetGrain<IOrderGrain>(orderId).AsReference<IDurableTaskGrainExtension>();
        await foreach (var (taskId, state) in runtime.GetTasksAsync())
        {
            Console.WriteLine($"{taskId}: {state.Status} {state.Request} -> {state.Response}");
        }
    }
    // </durable_tasks_diagnostics>
}
