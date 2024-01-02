using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Journaling;
using Azure.Storage.Blobs;
using Azure.Core.Pipeline;
using Azure.Core;
using System.Diagnostics;
using Newtonsoft.Json;
using System.Distributed.DurableTasks;
using Orleans.DurableTasks;
using Orleans.Runtime.DurableTasks;
using WorkflowsApp.Service.Samples.HelloWorld;
using WorkflowsApp.Service.Samples.CancelWorld;
using WorkflowsApp.Service.Samples.Bank;
using WorkflowsApp.Service.Samples.Parallelism;
using WorkflowsApp.Service.Samples.HumanInTheLoop;

namespace WorkflowsApp.Service;
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously

public static class DurableTaskHostingExtensions
{
    public static ISiloBuilder AddVolatileDurableTaskStorage(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddTransient<VolatileDurableTaskGrainStorage>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainStorage, VolatileDurableTaskGrainStorage>();
        return siloBuilder;
    }

    public static ISiloBuilder AddJournaledDurableTaskStorage(this ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.TryAddSingleton<DurableTaskGrainStorageShared>();
        siloBuilder.Services.TryAddScoped<DurableTaskGrainStorage>();
        siloBuilder.Services.AddFromExisting<IDurableTaskGrainStorage, DurableTaskGrainStorage>();
        return siloBuilder;
    }
}

public class Program
{

    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Host.UseDefaultServiceProvider(o => o.ValidateOnBuild = false);
        builder.AddKeyedAzureBlobClient("state");
        builder.AddKeyedAzureTableClient("clustering");
        builder.UseOrleans(siloBuilder =>
            {
                //siloBuilder.UseLocalhostClustering();
                siloBuilder.AddDurableTasks();
                siloBuilder.AddStateMachineStorage();
                siloBuilder.AddJournaledDurableTaskStorage();
                siloBuilder.AddAzureAppendBlobStateMachineStorage();
                siloBuilder.Services.AddOptions<AzureAppendBlobStateMachineStorageOptions>().Configure((AzureAppendBlobStateMachineStorageOptions options, IServiceProvider serviceProvider)
                    => options.ConfigureBlobServiceClient(ct => Task.FromResult(serviceProvider.GetRequiredKeyedService<BlobServiceClient>("state"))));
            });

        //logging.AddFilter((category, level) => category is not null && category.StartsWith("Orleans.DurableTasks"));
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        using var app = builder.Build();

        HumanInTheLoop.ConfigureApp(app);

        await app.StartAsync();

        //await HumanInTheLoop.RunAsync(app.Services);
        await SumOfSquares.RunAsync(app.Services);
        await Bank.RunAsync(app.Services);
        await HelloWorld.RunAsync(app.Services);
        await CancelWorld.RunAsync(app.Services);

        /*
        var client = host.Services.GetRequiredService<IClusterClient>();

        var dict = client.GetGrain<IDictionaryGrain<string, int>>("foo");

        await dict.TryAddAsync("one", 1);
        var dict = client.GetGrain<IDictionaryGrain<string, int>>("foo");

        await dict.TryAddAsync("one", 1);
        var (success, value) = await dict.TryGetValueAsync("one");
        value++;
        await dict.SetAsync("one", value);
        Console.WriteLine($"[one] = {value}");
        for (var i = 0; i < 10; i++)
        {
            var key = $"{i}";
            (success, value) = await dict.TryGetValueAsync(key);
            value++;
            await dict.SetAsync(key, value);
        }

        await foreach (var (k, v) in dict.GetValuesAsync())
        {
            Console.WriteLine($"[{k}] = {v}");
        }
        */

        Console.WriteLine("Done!");

        await app.WaitForShutdownAsync();
    }

#if false
    public static async Task Main(string[] args)
    {
        var prog = new Program();

        /*
        var result = await prog.GoBeDurable("Bob", 47);
        Console.WriteLine(result);
        */

        // The above is shorthand for the following.

        // Define a task. No task code will be executed until it is scheduled.
        DurableTask<int> taskDefinition = prog.GoBeDurable("Bob", 47);

        // Schedule the task. This would write the definition above to storage, alongside the options specified here (retry, identity, etc)
        ScheduledTask<int> scheduledResult = await taskDefinition.ScheduleAsync(
            "foo",
            new SchedulingOptions
            {
                DueTime = DateTimeOffset.UtcNow,
                RetryOptions = new RetryOptions
                {
                    MaximumNumberOfAttempts = 3,
                    RetryFilter = RetryFilter
                }
            });

        // Await the completion of the scheduled task and print the result
        int finalResult = await scheduledResult;
        Console.WriteLine(finalResult);

        static bool RetryFilter(Exception exception)
        {
            return true;
        }
    }

    public async DurableTask<int> GoBeDurable(string name, int bestNumber)
    {
        // Await some task
        await Task.Yield();

        // Schedule a child task and await its result
        await GoToSleep(1000);

        // Return a result to the caller
        return bestNumber * name.GetHashCode();
    }

    public async DurableTask GoToSleep(int delayMillis)
    {
        await Task.Yield();
    }
#endif
}

[Alias("ICopyProcessorGrain")]
internal interface ICopyProcessorGrain : IGrain
{
    [Alias("Copy")]
    DurableTask Copy(string source, string destination, string startRowId, string endRowId);
}

internal interface IDbServiceFactory
{
    IDbService GetDb(string connectionString);
}

internal interface IDbService
{
    IAsyncEnumerable<(string Key, string Value)> ReadRangeAsync(string startRowId, string endRowId, int limit);
    ValueTask InsertOrUpdateRowAsync(string key, string value);
}

#if false
// Example: ETL - load data from one database and store it into another.
// This example uses a fake Database API for simplicity.
// 
internal class CopyProcessorGrain : Grain, ICopyProcessorGrain
{
    private readonly IDbServiceFactory _dbServiceFactory;

    [GenerateSerializer]
    internal class CopyState
    {
        [Id(0)]
        public string? LastCopiedRow { get; set; }
    }

    public CopyProcessorGrain(IDbServiceFactory dbServiceFactory)
    {
        _dbServiceFactory = dbServiceFactory;
    }

    public async DurableTask Copy(string source, string destination, string startRowId, string endRowId)
    {
        var ctx = DurableTaskContext.CurrentTask!;
        var sourceDb = _dbServiceFactory.GetDb(source);
        var destinationDb = _dbServiceFactory.GetDb(destination);

        var state = ctx.GetState<CopyState>("currentRow");
        state.Value.LastCopiedRow ??= startRowId;

        while (!ctx.IsCancellationRequested)
        {
            var hasRows = false;
            await foreach (var (rowKey, rowValue) in sourceDb.ReadRangeAsync(startRowId: state.Value.LastCopiedRow, endRowId: endRowId, limit: 100))
            {
                await destinationDb.InsertOrUpdateRowAsync(rowKey, rowValue);
                state.Value.LastCopiedRow = rowKey;
                hasRows = true;
            }

            if (!hasRows)
            {
                // Done!
                break;
            }

            // Update the state of the workflow in case it gets terminated and needs to restart.
            // This does not need to happen for every iteration: it's ok to only perform it occasionally (eg, every 100 iterations) to improve performance.
            await state.WriteStateAsync();
        }
    }
}
#endif

#if false
public interface IStateManager
{
    IPersistentState<T> GetState<T>(string name);
    ValueTask WriteStateAsync();
}


// Example: soft-delete with a 30-day delayed hard-delete
public interface ISubscriptionGrain : IGrain
{
    Task Subscribe(IAccountGrain account);
    Task CancelSubscription();
}

interface ISubscriptionGrainInternal : IGrain
{
    DurableTask ProcessSubscription();
    DurableTask ProcessCancellation();
}

[GenerateSerializer]
public class CustomerAccount
{
    [Id(0)] public string? AccountId { get; set; }
}

[GenerateSerializer]
public class SubscriptionGrainState
{
    [Id(0)] public DateTime NextBillingCycle { get; set; }
    [Id(1)] public DateTime? CurrentBillingCycleStart { get; set; }
    [Id(2)] public DateTime? CanceledSince { get; set; }
    [Id(3)] public SubscriptionStatus SubscriptionStatus { get; set; }
    [Id(4)] public CustomerAccount? Account { get; set; }
    [Id(5)] public string? NextBillingProcessId { get; set; }
}

[GenerateSerializer]
public record class CurrencyAmount(double Amount, string Currency);

public enum SubscriptionStatus
{
    None,
    Valid,
    Canceled,
    PaymentError,
}

public class SubscriptionGrain : Grain<SubscriptionGrainState>, ISubscriptionGrain
{
    const string SubscriptionTaskName = "process";
    const string CancelTaskName = "cancel";
    const double Fee = 100.0;
    static readonly TimeSpan BillingPeriod = TimeSpan.FromDays(30);

    readonly IPaymentGateway _paymentGateway;

    public SubscriptionGrain(IPaymentGateway paymentGateway)
    {
        _paymentGateway = paymentGateway;
    }

    public async Task Subscribe(CustomerAccount account)
    {
        State.Account = account;
        State.CurrentBillingCycleStart = DateTime.UtcNow;
        State.NextBillingCycle = State.CurrentBillingCycleStart.Value.Add(BillingPeriod);
        await WriteStateAsync();

        await this.AsReference<ISubscriptionGrainInternal>()
          .ProcessSubscription()
          .ScheduleAsync(SubscriptionTaskName);
    }

    public async Task CancelSubscription(CustomerAccount account)
    {
        await this.AsReference<ISubscriptionGrainInternal>()
          .ProcessCancellation()
          .ScheduleAsync("cancel");
    }

    public async DurableTask ProcessCancellation()
    {
        // If the subscription was already paused before we started execution, remember that fact and bail out.
        var alreadyCanceled = await DurableTaskContext.CurrentTask!.GetOrAddStateAsync<bool>("already-canceled", State.CanceledSince.HasValue);
        if (alreadyCanceled.Value)
        {
            return;
        }

        var canceledSince = await DurableTaskContext.CurrentTask!.GetOrAddStateAsync<DateTime>("canceled-since", DateTime.UtcNow);
        State.CanceledSince = canceledSince.Value;

        // Since the state was updated, we need to persist that change.
        await WriteStateAsync();

        // Get a task from the list of scheduled tasks on the instance.
        await GetTask(SubscriptionTaskName).CancelAsync();

        // We are a friendly company, so we will refund the remaining amount.
        if (State.CurrentBillingCycleStart.HasValue && State.SubscriptionStatus == SubscriptionStatus.Valid)
        {
            var refundDays = State.CurrentBillingCycleStart.Value + BillingPeriod - canceledSince.Value;
            var refundAmount = Fee * refundDays.TotalDays / BillingPeriod.TotalDays;
            await ProcessPayment(new CurrencyAmount(refundAmount, "USD")).AsStep("process-refund");
        }
    }

    public async DurableTask ProcessSubscription()
    {
        try
        {
            // Try charging the account.
            // If successful, update the status to valid and schedule the subsequent billing cycle.
            await ProcessPayment(new CurrencyAmount(Fee, "USD")).AsStep("process-payment");
            State.SubscriptionStatus = SubscriptionStatus.Valid;
            State.CurrentBillingCycleStart = State.NextBillingCycle;
            State.NextBillingCycle = State.NextBillingCycle.Add(BillingPeriod);

            // Schedule to charge the customer again at the start of the next billing cycle.
            await this.AsReference<ISubscriptionGrainInternal>()
              .ProcessSubscription()
              // The semantics of this must be that it reschedules the task with the specified id, clearing its state
              // Should there be an API to differentiate 
              .ScheduleAsync(SubscriptionTaskName, State.NextBillingCycle);
        }
        catch
        {
            // Indicate that the account has not been paid and try again in a day.
            State.SubscriptionStatus = SubscriptionStatus.PaymentError;
            var nextAttempt = DateTime.UtcNow.Date.AddDays(1);

            // Schedule to charge the customer again at the start of the next billing cycle.
            await this.AsReference<ISubscriptionGrainInternal>()
              .ProcessSubscription()
              .ScheduleAsync(SubscriptionTaskName, nextAttempt);
            return;
        }
    }

    private async DurableTask ProcessPayment(CurrencyAmount amount)
    {
        // Generate an idempotency key for the payments API
        // This is used to ensure exactly-once processing of payments.
        var key = await DurableTaskContext.CurrentTask!.GetOrAddStateAsync<string>(
            "payment-idempotency-key",
            static () => Guid.NewGuid().ToString("N"));

        await _paymentGateway.ProcessCharge(
          customer: State.Account,
          amount: amount.Amount,
          currency: amount.Currency,
          idempotencyKey: key.Value);
    }
}

// Experiment - using a different base class for orchestrators
public abstract class DurableTaskOrchestrator
{
}

public interface ISubscriptionProcessor
{
    Task CreateSubscription();
    Task PauseSubscription(DateTimeOffset until);
    Task ResumeSubscription();
    Task UpdateBillingInformation();
    Task GetCurrentStatus();
}

public class SubscriptionProcessor : DurableTaskOrchestrator
{
    DurableTask Run(DurableTaskContext context);
}
#endif

// Example: eShop order process
[Alias("IBuyerAccount")]
public interface IBuyerAccount : IGrain { }

[GenerateSerializer]
[Alias("Invoice")]
public record class Invoice();

[GenerateSerializer]
[Alias("PaymentResult")]
public record class PaymentResult()
{
    public bool IsSuccess { get; internal set; }
}

public interface IPaymentService
{
    DurableTask<Invoice> CreateInvoice(IBuyerAccount buyer, Order order);
    DurableTask<PaymentResult> WaitForPayment(Invoice invoice);
}

[GenerateSerializer]
[Alias("StockCheckResult")]
public record class StockCheckResult(bool HasStock);

[GenerateSerializer]
[Alias("CreateShipmentResult")]
public record class CreateShipmentResult(bool IsSuccess);

public interface ICatalogService
{
    DurableTask<List<StockCheckResult>> CheckOrderStock(Order order);
}
public interface ILogisticsService
{
    DurableTask<CreateShipmentResult> CreateShipment(Order order);
    DurableTask WaitForDelivery(CreateShipmentResult shipmentDetails);
}
[GenerateSerializer]
[Alias("Order")]
public record class Order();

[Alias("IOrderProcessor")]
public interface IOrderProcessor : IGrain
{
    [Alias("ProcessOrderAsync")]
    DurableTask ProcessOrderAsync(IBuyerAccount buyer, Order order);
}

[GenerateSerializer]
public enum OrderStatus
{
    None,
    Created,
    Confirmed,
    Canceled,
    Paid,
    Shipped,
    Delivered,
    InsufficientStock,
    PaymentFailed,
    ShipmentFailed,
}

[GenerateSerializer]
[Alias("OrderState")]
public class OrderState
{
    [Id(0)]
    public OrderStatus Status { get; set; }
}

public class OrderProcessorGrain(
    IPaymentService paymentService,
    ICatalogService catalogService,
    ILogisticsService logisticsService) : Grain<OrderState>, IOrderProcessor
{
    private readonly IPaymentService _paymentService = paymentService;
    private readonly ICatalogService _catalogService = catalogService;
    private readonly ILogisticsService _logisticsService = logisticsService;

    public async DurableTask ProcessOrderAsync(IBuyerAccount buyer, Order order)
    {
        /*
        var confirmed = await DurableTask.Delay(TimeSpan.FromMinutes(1)).WithId("wait-for-confirmation");
        if (!confirmed)
        {
            // The order was canceled using task management APIs within the grace period.
            State.Status = OrderStatus.Canceled;
            return;
        }
        */

        var stockLevelResult = await _catalogService.CheckOrderStock(order).WithId("check-stock");
        if (stockLevelResult.Any(item => !item.HasStock))
        {
            // There is insufficient stock. No charge has been made yet, but we would likely need to notify the user before terminating.
            State.Status = OrderStatus.InsufficientStock;
            return;
        }

        var invoice = await _paymentService.CreateInvoice(buyer, order).WithId("create-invoice");

        // This might take a very long time (hours, days, indefinite)
        var paymentResult = await _paymentService.WaitForPayment(invoice).WithId("process-payment");
        if (!paymentResult.IsSuccess)
        {
            State.Status = OrderStatus.PaymentFailed;
            return;
        }

        State.Status = OrderStatus.Paid;
        var shipmentDetails = await _logisticsService.CreateShipment(order).WithId("create-shipment");
        if (!shipmentDetails.IsSuccess)
        {
            State.Status = OrderStatus.ShipmentFailed;
            return;
        }

        await _logisticsService.WaitForDelivery(shipmentDetails).WithId("wait-for-delivery");
        State.Status = OrderStatus.Delivered;

        // Done...
    }
}

/*
public class TransferGrain : ITransferGrain
{
    public interface ITransactionContext : IAsyncDisposable, IDisposable
    {
        ValueTask CommitAsync();
        ValueTask AbortAsync();
    }

    public bool StartTransaction(string key, [NotNullWhen(true)] out ITransactionContext? transaction)
    {
        transaction = default;
        return false;
    }

    public async DurableTask<bool> Transfer(IAccountGrain source, IAccountGrain destination, int amount)
    {
        if (StartTransaction("step1", out var txn))
        {
            var state = await _state.JoinWriteTransaction();

            await txn.CommitAsync();
        }
        var ctx = ScheduledTaskContext.Current!;

        // Durably schedule the debit task and wait for completion. The fixed id ensures that exactly one operation will be scheduled.
        // Subsequent calls (eg, during recovery) will receive a reference the same logical task.
        bool fundsAvailable = await source.TryDebit(amount).InvokeAsync(ctx.Id + "/debit");

        if (fundsAvailable)
        {
            await destination.Credit(amount).InvokeAsync(ctx.Id + "/credit");
        }

        return fundsAvailable;
    }
}

public interface IScheduledTaskCollection<TInterface> where TInterface : class
{
    void Defer(Func<TInterface, DurableTask> taskFunc);
    ValueTask CommitAsync();
}

public class AccountState { public int Balance { get; set; } }

public class AccountGrain : IAccountGrain
{
    private readonly ITransactionalState<AccountState> _state;
    public AccountGrain(ITransactionalState<AccountState> state) => _state = state;

    public async DurableTask<bool> TryDebit(int amount)
    {
        // Get the durable, mutable task context.
        var context = ScheduledTaskContext.Current!;

        // Enter a transaction. The result of the transactional invocation of the delegate
        // will be transactionally stored as "debitAmount" on the ScheduledTaskContext.
        // I.e, the transaction participants are ScheduledTaskContext and _state.
        // During recovery, if the value is present in the context, then the delegate will not be invoked a second time.
        var result = await context.GetOrAddInTransaction(
            "debitAmount",
            TransactionOption.CreateOrJoin,
            async () =>
            {
                // Join the transaction as a writer
                var currentValue = await _state.JoinTransaction(readOnly: false);

                if (currentValue.Balance >= amount)
                {
                    currentValue.Balance -= amount;
                    return true;
                }

                return false;
            });

        return result;
    }

    public async DurableTask Credit(int amount)
    {
        // Get the durable, mutable task context.
        var context = ScheduledTaskContext.Current!;

        // Enter a transaction
        await context.GetOrAddInTransaction(
            "creditAmount",
            TransactionOption.CreateOrJoin,
            async () =>
            {
                // Join the transaction as a writer
                var currentValue = await _state.JoinTransaction(readOnly: false);
                currentValue.Balance += amount;
            });
    }
}


public static class TransactionalStateExtensions
{
    public static ValueTask<IAsyncDisposable> EnterWriteTransaction<T>(this ITransactionalState<T> state, TransactionOption option) where T : class, new()
    {
        _ = option;
        _ = state;
        return default;
    }
}

#endif
*/


[Alias("IMyGrainWithDurableTasks")]
public interface IMyGrainWithDurableTasks : IGrain
{
    [Alias("MyDurableTaskMethod")]
    DurableTask MyDurableTaskMethod(int a, string b);
    [Alias("MyDurableTaskMethod2")]
    DurableTask<string> MyDurableTaskMethod2(int a, string b);
    [Alias("MyDurableTaskMethod3")]
    DurableTask<T> MyDurableTaskMethod3<T>(T a);
}

internal partial class LoggingPolicy(ILoggerFactory loggerFactory) : HttpPipelinePolicy
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("WorkflowApp");

    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) => ProcessAsync(message, pipeline, false).GetAwaiter().GetResult();

    public override ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline) => ProcessAsync(message, pipeline, true);

    private void LogRequest(HttpMessage request)
    {
        if (request!.Request!.Content?.TryComputeLength(out var len) is true)
        {
            _logger.LogInformation("Request {Length} bytes", len);
        }
        else
        {
            _logger.LogInformation("Request of unknown length");
        }
    }

    private async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline, bool async)
    {
        LogRequest(message);

        var before = Stopwatch.GetTimestamp();

        try
        {
            if (async)
            {
                await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
            }
            else
            {
                ProcessNext(message, pipeline);
            }
        }
        catch
        {
            throw;
        }

        var after = Stopwatch.GetTimestamp();

        var response = message.Response;
        var isError = response.IsError;

        var elapsed = (after - before) / (double)Stopwatch.Frequency;

        if (isError)
        {
            _logger.LogInformation("Response error");
        }
        else
        {
            _logger.LogInformation("Response length {ResponseLength}", response.Headers.ContentLength);
        }
    }
}
