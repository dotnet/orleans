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
        await SetStatus(OrderStatus.Submitted).WithId("submit");

        // <durable_tasks_fan_out>
        bool[] reserved;
        try
        {
            await DurableTask.Delay(TimeSpan.FromMinutes(1)).WithId("grace-period");
            await SetStatus(OrderStatus.AwaitingValidation).WithId("validate");

            // Reserves every line concurrently, as the children reserve/0, reserve/1, and so on.
            reserved = await DurableTask.WhenAll(
                    order.Lines.Select(line => Catalog(line.ProductId).ReserveAsync(OrderId, line.Units)))
                .WithId("reserve");
        }
        catch (OperationCanceledException) when (DurableExecutionContext.Current!.IsCancellationRequested)
        {
            // WhenAll has waited for every reservation, so none is still running.
            return await CancelOrderAsync(order.Lines, DurableExecutionContext.Current.CancellationReason);
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

        await SetStatus(OrderStatus.Paid).WithId("pay");

        // Returns at once when the shipment arrived before the wait.
        var shipment = await Shipped.WaitAsync();
        await SetStatus(OrderStatus.Shipped).WithId("ship");
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
        // The workflow's cancellation doesn't reach children scheduled in this scope.
        using (DurableExecutionContext.SuppressCancellation())
        {
            await DurableTask.WhenAll(lines.Select(line => Catalog(line.ProductId).ReleaseAsync(OrderId)))
                .WithId("release");
            await SetStatus(OrderStatus.Cancelled).WithId("cancel");
            return new OrderResult(OrderStatus.Cancelled, reason);
        }
    }
    // </durable_tasks_compensation>

    // <durable_tasks_local_step>
    // A local step: its state change commits with its recorded result, which a replay returns.
    private async DurableTask<OrderStatus> SetStatus(OrderStatus status)
    {
        _status.Value = status;
        return status;
    }
    // </durable_tasks_local_step>

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
    private readonly IDurableTaskManager _tasks;
    private readonly IDurableDictionary<int, OrderLine> _lines;

    public BasketGrain(IDurableTaskManager tasks)
    {
        _tasks = tasks;
        _lines = StateManager.GetOrAddDictionary<int, OrderLine>("lines");
    }

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

        // Stages the invocation in the outbox and writes the journal, so the cleared basket commits with it.
        await GrainFactory.GetGrain<IOrderGrain>(orderId)
            .PlaceOrderAsync(order)
            .ScheduleAsync(OrderGrain.PlaceOrderTaskId(orderId));
        return orderId;
    }

    // Reads this grain's record of the call, which receives the workflow's result through the inbox.
    public async Task<OrderResult?> GetOrderResultAsync(Guid orderId)
    {
        try
        {
            var response = await _tasks.GetTask<OrderResult>(OrderGrain.PlaceOrderTaskId(orderId))
                .GetResponseAsync(new PollingOptions { PollTimeout = TimeSpan.Zero });
            return response.Status is DurableTaskStatus.CompletedSuccessfully ? response.GetResult<OrderResult>() : null;
        }
        catch (DurableTaskNotFoundException)
        {
            // No checkout scheduled this order, or the result's retention period has ended.
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
        // Scheduling the same request under the same root ID again returns the same task.
        ScheduledTask<OrderResult> workflow = await client.GetGrain<IOrderGrain>(orderId)
            .PlaceOrderAsync(order)
            .ScheduleAsync(OrderGrain.PlaceOrderTaskId(orderId));
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

    DurableTask<ShippingQuote[]> CollectQuotesAsync(string route, string[] carriers, int count);
}

public sealed partial class ShippingGrain : DurableGrain, IShippingGrain
{
    public async DurableTask<ShippingQuote?> QuoteFirstAsync(string route)
    {
        var (_, quote) = await DurableTask.WhenAny(
            [
                Carrier("ground").QuoteAsync(route),
                Carrier("air").QuoteAsync(route),
                QuoteDeadline(TimeSpan.FromMinutes(2)),
            ])
            .WithId("first-quote");
        return quote;
    }

    // Returns null when the deadline wins the race.
    private static async DurableTask<ShippingQuote?> QuoteDeadline(TimeSpan timeout)
    {
        await DurableTask.Delay(timeout);
        return null;
    }

    private ICarrierGrain Carrier(string name) => GrainFactory.GetGrain<ICarrierGrain>(name);
}
// </durable_tasks_race>

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
        using (DurableExecutionContext.SuppressCancellation())
        {
            await DurableTask.WhenAll(held.Select(line => Catalog(line.ProductId).ReleaseAsync(OrderId)))
                .WithId("release");
        }

        DurableExecutionContext.Current!.CancellationToken.ThrowIfCancellationRequested();
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
        var deadline = DurableExecutionContext.Current!.UtcNow.AddDays(3);
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

    public static string IssueTaskId(Guid invoiceId) => $"issue-{invoiceId:N}";

    public async DurableTask<decimal> IssueAsync(InvoiceRequest request)
    {
        var amount = await Record(request.Amount).WithId("record");

        // Starts the root task "issue-{invoiceId}:receipt" on the notification grain without waiting for it.
        await GrainFactory.GetGrain<INotificationGrain>(request.CustomerId)
            .SendReceiptAsync(this.GetPrimaryKey(), amount)
            .DetachAsync(DetachedTaskId.For("receipt"));
        return amount;
    }

    private async DurableTask<decimal> Record(decimal amount) => _issued.Value = amount;
}
// </durable_tasks_detach>

internal static class InvoiceClient
{
    // <durable_tasks_detach_observe>
    public static async Task<DurableTaskStatus> GetReceiptStatusAsync(IClusterClient client, Guid invoiceId, string customerId)
    {
        var rootId = DetachedTaskId.For(TaskId.CreateRoot(InvoiceGrain.IssueTaskId(invoiceId)), "receipt");
        var receipt = client.GetGrain<INotificationGrain>(customerId).GetDurableTask<bool>(rootId);
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
