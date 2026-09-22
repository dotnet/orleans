# Microsoft Orleans Durable Jobs

## Introduction
Microsoft Orleans Durable Jobs provides a distributed, scalable system for scheduling one-time jobs that execute at a specific time. Unlike Orleans Reminders which are designed for recurring tasks, Durable Jobs are ideal for one-time future events such as appointment notifications, delayed processing, scheduled workflow steps, and time-based triggers.

**Key Features:**
- **At Least One-time Execution**: Jobs are scheduled to run at least once
- **Persistent**: Jobs survive grain deactivation and silo restarts
- **Distributed**: Jobs are automatically distributed and rebalanced across silos
- **Reliable**: Failed jobs can be automatically retried with configurable policies
- **Rich Metadata**: Associate custom metadata with each job
- **Durably cancellable**: Cancellation requests prevent future attempts; an already-running attempt may still complete

## Getting Started

### Installation
To use this package, install it via NuGet:

```shell
dotnet add package Microsoft.Orleans.DurableJobs
```

For production scenarios with persistence, also install a storage provider:

```shell
dotnet add package Microsoft.Orleans.DurableJobs.AzureStorage
```

### Configuration

#### Using In-Memory Storage (Development/Testing)
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        // Configure in-memory Durable Jobs (no persistence)
        .UseInMemoryDurableJobs();
});

await builder.Build().RunAsync();
```

#### Using Azure Storage (Production)
```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        // Configure Azure Storage Durable Jobs
        .UseAzureBlobDurableJobs(options =>
        {
            options.BlobServiceClient = new Azure.Storage.Blobs.BlobServiceClient("YOUR_CONNECTION_STRING");
            options.ContainerName = "durable-jobs";
        });
});

await builder.Build().RunAsync();
```

#### Advanced Configuration
```csharp
builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseLocalhostClustering()
        .UseInMemoryDurableJobs()
        .ConfigureServices(services =>
        {
            services.Configure<DurableJobsOptions>(options =>
            {
                // Duration of each job shard (jobs are partitioned by time)
                options.ShardDuration = TimeSpan.FromMinutes(5);

                // Load eligible shards within this horizon and check at this interval
                options.ShardLoadLookaheadPeriod = TimeSpan.FromMinutes(10);
                options.ShardCheckInterval = TimeSpan.FromMinutes(5);
                
                // Maximum number of jobs that can execute concurrently on each silo
                options.MaxConcurrentJobsPerSilo = 100;
                
                // Custom retry policy
                options.ShouldRetry = (context, exception) =>
                {
                    // Retry up to 3 times with exponential backoff
                    if (context.DequeueCount < 3)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, context.DequeueCount));
                        return DateTimeOffset.UtcNow.Add(delay);
                    }
                    return null; // Don't retry
                };
            });
        });
});
```

## Select named journal storage

`UseJournaledDurableJobs` on `ISiloBuilder` or `IServiceCollection` installs the
journaled shard manager and Durable Jobs JSON metadata independently of the
storage backend. Register a catalog-capable journal provider, then select it
with `DurableJobsOptions.ActiveProviderName`. The default name is `"Default"`.
`DrainingProviderNames` is initially empty.

```csharp
siloBuilder
    .AddAzureBlobJournalStorage("jobs-a", options =>
    {
        options.BlobServiceClient = originalClient;
        options.ContainerName = "jobs-original";
    })
    .AddAzureBlobJournalStorage("jobs-b", options =>
    {
        options.BlobServiceClient = currentClient;
        options.ContainerName = "jobs-current";
    })
    .UseJournaledDurableJobs(options =>
    {
        options.ActiveProviderName = "jobs-b";
        options.DrainingProviderNames.Add("jobs-a");
    });
```

Named Blob, Table, Redis, S3, and volatile registrations bind storage, catalog,
and state-manager factory to the same configured namespace. Explicit names
leave the default provider for grain journaling independent. Keep each physical
namespace registered under one selected name. `UseInMemoryDurableJobs`,
`UseAzureBlobDurableJobs`, and `UseAzureTableDurableJobs` remain convenience
compositions for the default binding. Volatile storage retains jobs for the
lifetime of its process; use persistent storage for restart recovery.

The `"Default"` storage binding retains the existing unnamed backend-options
pipeline; non-default bindings such as `"jobs-a"` use named backend options.

Most deployments select one provider. An uncached known-shard lookup then
reads that provider's metadata directly, irrespective of other journal providers
registered for grain state. During migration, discovery covers the write and
draining providers, and uncached known-ID lookups read the write provider first,
then draining providers as needed. An unavailable provider surfaces a lookup
failure; absence requires successful checks across all selected providers.

New schedules use shards created in the write provider. Existing shards retain
their original provider for ownership, replay, execution, retry, rescheduling,
cancellation, compaction, and deletion. Timestamp/GUID shard IDs,
`jobs/shards/<id>` paths, and provider-independent serialized `DurableJob`
handles stay unchanged. A shard has one authoritative storage location
throughout its lifetime.

### Cut over and retire a provider

Bindings are fixed at startup. For an A-to-B migration:

1. Deploy both bindings and select A for writes and B for draining on **every**
   scheduling silo before any silo creates B work.
2. Roll out B for writes and A for draining. A-configured silos can still create
   A shards during this rollout. Cutover finishes when every scheduling silo
   uses B and prior scheduling calls have finished. Pause application scheduling
   during the change if a strict cutover boundary is required.
3. Retain mutation permissions on A. Draining requires listing, reads, claims,
   durable updates, retries, cancellation, compaction, and deletion.
4. After all writers have cut over, inspect A's complete namespace using
   `IDurableJobsStorageInspector.InspectAsync("jobs-a", cancellationToken)`.
   Resolve all remaining shards, including future-dated, owned, poisoned, and
   unrecognized entries. Repeat successful inventories according to the
   backend's live-listing behavior.
5. Remove A from `DrainingProviderNames` only after a verified full drain.
   Retire the binding/storage only when its other consumers have also finished.

The inspector returns `DurableJobsStorageStatus` with `ProviderName`,
`IsWriteProvider`, `ShardCount`, `OwnedShardCount`, `PoisonedShardCount`,
`UnrecognizedShardCount`, and nullable `OldestShardStartTime` /
`NewestShardStartTime`. It reads a complete catalog snapshot through the calling
process using read-only catalog and metadata operations. Failures fault the
call: display **unknown** when inspection fails. Counts describe shards, each
of which can contain many jobs; owned/poisoned counts can overlap. Retirement
requires successful full-zero inventory plus completed cluster-wide writer
cutover.

To roll back, keep both providers selected and switch writes to A with B
draining. Existing B jobs continue in B. Preserve storage namespaces and
compatible journal readers throughout the migration.

See the [restart-based migration sample](../../samples/DurableJobsMigration/README.md)
for durable Azure emulator storage, retained-handle cancellation, and local
inventory checks, and the [migration guide](../../docs/site/src/content/docs/grains/journaling/durable-jobs-migration.md)
for deployment and monitoring details.

## Shard discovery and lookahead

Each silo discovers shards whose start time is within `DurableJobsOptions.ShardLoadLookaheadPeriod`
of its current Durable Jobs time-provider clock. The default lookahead is ten minutes.
A discovered shard starts processing once its start time enters `ShardActivationBufferPeriod`.
`DurableJobsOptions.ShardCheckInterval` controls periodic discovery and writable-shard cleanup
checks, with a default of five minutes. Membership changes also trigger checks.
The lookahead accepts non-negative durations; zero selects shards whose start time is at or
before the current time. The discovery horizon is capped at `DateTimeOffset.MaxValue`.
The check interval accepts durations from 1 to 4294967294 milliseconds.

Shard journals use names such as
`jobs/shards/20260909T1200000000000Z-<unique-id>`. The fixed-width UTC start time
precedes the unique suffix, so ordinal name order is shard-start-time order. Each sweep
lists the raw `jobs/shards/` prefix with an inclusive `JournalCatalogListOptions.MaxId` bound covering the lookahead
horizon. The range includes every earlier start time, including jobs overdue after a long
outage. Future shard identities are filtered by the catalog before candidate metadata reads.

Each periodic or membership check starts a fresh, locally scoped sweep over every selected
provider. Candidates share an oldest-first ordering and aggregate claim budget.
Discovery enumerates the selected catalogs in configured order, requesting metadata and
buffering each provider's candidates until that enumeration succeeds. Once all selected
catalogs have completed or faulted, discovery orders and deduplicates the successful
results, then uses each supplied
ownership snapshot with an ETag or reads current metadata when that snapshot is unavailable.
For a snapshot naming the local silo as owner, discovery reuses the cached shard or reads
current metadata on a cache miss. The refreshed descriptor determines eligibility, ownership,
and any required conditional claim before a new instance is opened.
Claims run oldest first and require the snapshot's ETag for conditional updates, so a
concurrent ownership change rejects a stale claim. Providers used for Durable Jobs supply
metadata ETags and enforce conditional updates; a missing ETag surfaces as a discovery error.
Assigned shards are delivered as they are opened, allowing execution to proceed while later
candidates are evaluated. The claim budget limits new claims;
locally owned shards remain eligible after that budget is exhausted.

Catalog providers apply raw-prefix and range constraints using their storage capabilities.
The timestamp representation also supports narrower day/hour prefixes and inclusive `MinId`
and `MaxId` intervals for callers selecting a specific time window. Recovery starts at the
shard namespace's beginning so that all overdue jobs remain eligible.
Azure Table's default mapping uses indexed key ranges. Azure Blob and ordered general-purpose
S3 listings can seek lower bounds and stop at upper bounds. S3 Express and Redis filter time
bounds during their provider-defined traversal. Discovery orders the selected names itself
to provide consistent oldest-first processing across providers. Storage listing work and request
latency remain provider-dependent. Blob, Table, and Volatile catalogs supply metadata snapshots
alongside identities; S3 and Redis require separate candidate metadata reads.

The sweep owns its enumeration and selected identity set until completion. During migration,
a catalog failure discards that provider's partial results; an assignment failure skips its
remaining candidates. Errors are reported with the provider name, and a later check retries
with a fresh sweep. Recovery latency includes every selected catalog's listing requests
and storage-client retry delays. Configure request timeouts and retry limits on the storage
clients to match the recovery latency requirements. Shards
already delivered to the local manager are tracked before cancellation is observed and
continue through their execution lifecycle.
Cancellation flows through listing, metadata, and journal operations.

Shorter lookahead periods reduce early loading of recovered shards. Shorter check intervals
increase sweep frequency and reduce the wait for newly inserted or newly eligible shards.
The public `JobShardManager.AssignJobShardsAsync` method collects the same ordered discovery
stream into its full-result list.

## Shutdown lifecycle

Scheduling, cancellation requests, and activation use the shared
`Orleans.Internal.AdmissionGate` utility from `Orleans.Core` for lock-free admission.
Callers keep the returned readonly token in a `using` local and check `Entered` before
starting work; disposal releases the admission.
Each admitted token has one owner which disposes it exactly once. An atomic increment
reserves a count and observes the closing flag in the same operation; attempts which
observe closure release their count immediately. A preliminary check rejects callers
which observe closure before incrementing, so further arrivals leave the drain count
unchanged. Scheduling and cancellation requests hold admission through completion,
including ownership lookup and remote cancellation routing. Activation holds admission
until its execution task is published and queued.
Shutdown atomically sets the closing flag through `CloseAsync`, signals cancellation to
in-flight requests, and awaits these operations before snapshotting the running shards
and canceling execution. Callback failures are logged while shutdown continues draining
requests, awaiting execution, and releasing shards.

Successful scheduling and cancellation writes retain their result, and successful shard
creations remain owned even when cancellation races with their completion. Shutdown then
awaits the active sweep and every admitted shard's execution and cleanup. It unregisters
cached shards which remained inactive using the shutdown token, then disposes them. The
journaled provider releases populated shards for another silo to claim and deletes empty
shards, including creations which completed after request cancellation.

## Usage Examples

### Basic Job Scheduling

#### 1. Implement the IDurableJobHandler Interface
```csharp
using Orleans;
using Orleans.DurableJobs;

public interface INotificationGrain : IGrainWithStringKey
{
    Task ScheduleNotification(string message, DateTimeOffset sendTime);
    Task CancelScheduledNotification(CancellationToken requestCancellationToken);
}

public class NotificationGrain : Grain, INotificationGrain, IDurableJobHandler
{
    private readonly ILocalDurableJobManager _jobManager;
    private readonly ILogger<NotificationGrain> _logger;
    private DurableJob? _durableJob;

    public NotificationGrain(
        ILocalDurableJobManager jobManager,
        ILogger<NotificationGrain> logger)
    {
        _jobManager = jobManager;
        _logger = logger;
    }

    public async Task ScheduleNotification(string message, DateTimeOffset sendTime)
    {
        var userId = this.GetPrimaryKeyString();
        var metadata = new Dictionary<string, string>
        {
            ["Message"] = message
        };

        _durableJob = await _jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = this.GetGrainId(),
                JobName = "SendNotification",
                DueTime = sendTime,
                Metadata = metadata
            },
            CancellationToken.None);

        _logger.LogInformation(
            "Scheduled notification for user {UserId} at {SendTime} (JobId: {JobId})",
            userId, sendTime, _durableJob.Id);
    }

    public async Task CancelScheduledNotification(CancellationToken requestCancellationToken)
    {
        if (_durableJob is null)
        {
            _logger.LogWarning("No scheduled notification to cancel");
            return;
        }

        var cancellationRequested = await _jobManager.CancelAsync(_durableJob, requestCancellationToken);
        _logger.LogInformation(
            "Notification {JobId} cancellation request recorded: {CancellationRequested}",
            _durableJob.Id,
            cancellationRequested);

        if (cancellationRequested)
        {
            // No future attempt will start. An already-running attempt may still complete.
            _durableJob = null;
        }
    }

    // This method is called when the durable job executes
    public Task ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        var userId = this.GetPrimaryKeyString();
        var message = context.Job.Metadata?["Message"];

        _logger.LogInformation(
            "Sending notification to user {UserId}: {Message} (Job: {JobId}, Run: {RunId}, Attempt: {DequeueCount})",
            userId, message, context.Job.Id, context.RunId, context.DequeueCount);

        // Send the notification here
        // If this throws an exception, the job can be retried based on your retry policy
        
        _durableJob = null;
        return Task.CompletedTask;
    }
}
```

#### 2. Order Workflow with Multiple Jobs
```csharp
public interface IOrderGrain : IGrainWithGuidKey
{
    Task PlaceOrder(OrderDetails details);
    Task CancelOrder();
}

public class OrderGrain : Grain, IOrderGrain, IDurableJobHandler
{
    private readonly ILocalDurableJobManager _jobManager;
    private readonly IOrderService _orderService;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<OrderGrain> _logger;

    public OrderGrain(
        ILocalDurableJobManager jobManager,
        IOrderService orderService,
        IGrainFactory grainFactory,
        ILogger<OrderGrain> logger)
    {
        _jobManager = jobManager;
        _orderService = orderService;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async Task PlaceOrder(OrderDetails details)
    {
        var orderId = this.GetPrimaryKey();
        
        // Create the order
        await _orderService.CreateOrderAsync(orderId, details);
        
        // Schedule delivery reminder for 24 hours before delivery
        var reminderTime = details.DeliveryDate.AddHours(-24);
        await _jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = this.GetGrainId(),
                JobName = "DeliveryReminder",
                DueTime = reminderTime,
                Metadata = new Dictionary<string, string>
                {
                    ["Step"] = "DeliveryReminder",
                    ["CustomerId"] = details.CustomerId,
                    ["OrderNumber"] = details.OrderNumber
                }
            },
            CancellationToken.None);

        // Schedule order expiration if payment not received
        var expirationTime = DateTimeOffset.UtcNow.AddHours(24);
        await _jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = this.GetGrainId(),
                JobName = "OrderExpiration",
                DueTime = expirationTime,
                Metadata = new Dictionary<string, string>
                {
                    ["Step"] = "OrderExpiration"
                }
            },
            CancellationToken.None);
    }

    public async Task CancelOrder()
    {
        var orderId = this.GetPrimaryKey();
        await _orderService.CancelOrderAsync(orderId);
    }

    public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        var step = context.Job.Metadata!["Step"];
        var orderId = this.GetPrimaryKey();

        switch (step)
        {
            case "DeliveryReminder":
                await HandleDeliveryReminder(context, attemptCancellationToken);
                break;

            case "OrderExpiration":
                await HandleOrderExpiration(attemptCancellationToken);
                break;
        }
    }

    private async Task HandleDeliveryReminder(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        var customerId = context.Job.Metadata!["CustomerId"];
        var orderNumber = context.Job.Metadata["OrderNumber"];
        
        var notificationGrain = _grainFactory.GetGrain<INotificationGrain>(customerId);
        await notificationGrain.ScheduleNotification(
            $"Your order #{orderNumber} will be delivered tomorrow!",
            DateTimeOffset.UtcNow);
    }

    private async Task HandleOrderExpiration(CancellationToken attemptCancellationToken)
    {
        var orderId = this.GetPrimaryKey();
        var order = await _orderService.GetOrderAsync(orderId, attemptCancellationToken);
        
        if (order?.Status == OrderStatus.Pending)
        {
            await _orderService.CancelOrderAsync(orderId, attemptCancellationToken);
            _logger.LogInformation("Order {OrderId} expired and canceled", orderId);
        }
    }
}
```

### Advanced Scenarios

#### Job with Retry Logic
```csharp
public class PaymentProcessorGrain : Grain, IDurableJobHandler
{
    private readonly IPaymentService _paymentService;
    private readonly ILogger<PaymentProcessorGrain> _logger;

    public Task ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        var paymentId = context.Job.Metadata?["PaymentId"];
        
        _logger.LogInformation(
            "Processing payment {PaymentId} (Attempt {Attempt})",
            paymentId, context.DequeueCount);

        try
        {
            await _paymentService.ProcessPaymentAsync(paymentId, attemptCancellationToken);
            return Task.CompletedTask;
        }
        catch (TransientException ex)
        {
            _logger.LogWarning(ex, "Payment processing failed with transient error, will retry");
            throw; // Let the retry policy handle it
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Payment processing failed with permanent error");
            throw; // This will not be retried if the retry policy returns null
        }
    }
}
```

#### Tracking Job Completion
```csharp
public class WorkflowGrain : Grain, IDurableJobHandler
{
    private readonly Dictionary<string, TaskCompletionSource> _pendingJobs = new();

    public async Task<DurableJob> ScheduleWorkflowStep(string stepName, DateTimeOffset executeAt)
    {
        var job = await _jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = this.GetGrainId(),
                JobName = stepName,
                DueTime = executeAt,
                Metadata = null
            },
            CancellationToken.None);

        _pendingJobs[job.Id] = new TaskCompletionSource();
        return job;
    }

    public async Task WaitForJobCompletion(string jobId, TimeSpan timeout)
    {
        if (_pendingJobs.TryGetValue(jobId, out var tcs))
        {
            using var cts = new CancellationTokenSource(timeout);
            await tcs.Task.WaitAsync(cts.Token);
        }
    }

    public Task ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        // Execute the workflow step...
        
        // Mark as complete
        if (_pendingJobs.TryRemove(context.Job.Id, out var tcs))
        {
            tcs.SetResult();
        }

        return Task.CompletedTask;
    }
}
```

## How It Works

### Architecture Overview
1. **Job Sharding**: Jobs are partitioned into time-based shards (default: 1-minute windows)
2. **Shard Ownership**: Each shard is owned by a single silo for execution
3. **Automatic Rebalancing**: When a silo fails, its shards are automatically reassigned to healthy silos
4. **Ordered Execution**: Within a shard, jobs are processed in order of their due time
5. **Concurrency Control**: The `MaxConcurrentJobsPerSilo` setting limits concurrent job execution

### Job Lifecycle
```
┌─────────────┐
│  Scheduled  │ ──▶ Job is created and added to appropriate shard
└─────────────┘
      │
      ▼
┌─────────────┐
│   Waiting   │ ──▶ Job waits in queue until due time
└─────────────┘
      │
      ▼
┌─────────────┐
│  Executing  │ ──▶ Job handler is invoked on target grain
└─────────────┘
      │
      ├──▶ Success ──▶ Job is removed
      │
      └──▶ Failure ──▶ Retry policy decides:
                        • Retry: Job is re-queued with new due time
                        • No Retry: Job is removed
```

## Configuration Reference

### DurableJobsOptions

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ShardDuration` | `TimeSpan` | 1 minute | Duration of each job shard. Smaller values reduce latency but increase overhead. |
| `MaxConcurrentJobsPerSilo` | `int` | 100 | Maximum number of jobs that can execute simultaneously on a silo. |
| `ShouldRetry` | `Func<IJobRunContext, Exception, DateTimeOffset?>` | 3 retries with exp. backoff | Determines if a failed job should be retried. Return the new due time or `null` to not retry. |

## Best Practices

1. **Set Reasonable Concurrency Limits**: Prevent resource exhaustion
   ```csharp
   options.MaxConcurrentJobsPerSilo = 100; // Adjust based on your workload
   ```

2. **Implement Idempotent Job Handlers**: Jobs may be retried, ensure handlers are idempotent
   ```csharp
   public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
   {
       var jobId = context.Job.Id;
       // Check if already processed
       if (await _state.IsProcessed(jobId))
           return;
           
       // Process job...
       await _state.MarkProcessed(jobId);
   }
   ```

3. **Use Metadata Wisely**: Keep metadata lightweight
   ```csharp
   // Good: Store IDs
   var metadata = new Dictionary<string, string> { ["OrderId"] = "12345" };
   
   // Bad: Store large objects
   var metadata = new Dictionary<string, string> { ["Order"] = JsonSerializer.Serialize(largeOrder) };
   ```

4. **Handle Cancellation**: Respect the cancellation token
   ```csharp
   public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
   {
       await SomeLongRunningOperation(attemptCancellationToken);
   }
   ```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Timers and Reminders](https://dotnet.github.io/orleans/docs/grains/timers-and-reminders/)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
