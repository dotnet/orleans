# Microsoft Orleans Durable Jobs for Azure Storage

## Introduction
Microsoft Orleans Durable Jobs for Azure Storage persists scheduled jobs through the Azure Blob or Azure Table journal provider. Jobs survive silo restarts, grain deactivation, and cluster reconfigurations. `UseAzureBlobDurableJobs` configures append-blob WALs and block-blob checkpoints; `UseAzureTableDurableJobs` configures Table journal headers and data generations. Both register the journal-backed shard manager and durable-job JSON serialization metadata.

## Getting Started

### Installation
To use this package, install it via NuGet along with the core package:

```shell
dotnet add package Microsoft.Orleans.DurableJobs
dotnet add package Microsoft.Orleans.DurableJobs.AzureStorage
```

### Configuration

Choose `UseAzureBlobDurableJobs` with `AzureBlobJournalStorageOptions`, or
`UseAzureTableDurableJobs` with `AzureTableJournalStorageOptions`. Both support
`ISiloBuilder` and `IServiceCollection` registration. Table configuration accepts
a `TableServiceClient` and `TableName`; Blob configuration accepts a
`BlobServiceClient` and `ContainerName`. Clustering uses an appropriate Table
service, including a separate standard account when journal storage uses a
premium BlockBlobStorage account.

The [Durable Jobs journaling playground](../../../../playground/DurableJobsJournaling/README.md)
provides runnable Blob/Table backend selection. The
[Azure provider benchmarks](../../../../test/Benchmarks/Journaling/Azure/README.md)
measure append, checkpoint, recovery, and catalog workloads with bounded work
and explicit resource ownership.

### Named providers and account migration

For explicit storage selection, register
`AddAzureBlobJournalStorage("jobs-a", configure)` or
`AddAzureTableJournalStorage("jobs-a", configure)` and call
`UseJournaledDurableJobs(options => options.WriteProviderName = "jobs-a")`.
Both builder and service-collection APIs are supported. Each name has its own
backend options, storage provider, catalog, and state-manager factory. Named
registrations leave default grain journaling independent.

To move new work to another account, container, or table, retain A's original
namespace under `"jobs-a"` and register B's namespace under `"jobs-b"`:

```csharp
siloBuilder
    .AddAzureBlobJournalStorage("jobs-a", options =>
    {
        options.BlobServiceClient = originalAccountClient;
        options.ContainerName = "jobs";
    })
    .AddAzureBlobJournalStorage("jobs-b", options =>
    {
        options.BlobServiceClient = newAccountClient;
        options.ContainerName = "jobs";
    })
    .UseJournaledDurableJobs(options =>
    {
        options.WriteProviderName = "jobs-b";
        options.DrainingProviderNames.Add("jobs-a");
    });
```

The same pattern works with separate Table names/clients, or a Blob-to-Table
change. Register each physical namespace under one selected name; changing a
name's account or mapping while it contains work changes where those jobs can
be found.

With one selected provider, uncached shard lookup reads its metadata directly.
With B plus A selected, discovery reads both catalogs and known-ID lookup checks
B then A as needed. Existing jobs, including retries, reschedules, cancellation,
and deletion, continue writing A. New shards are created only in B. Shard IDs
and `DurableJob` handles remain timestamp/GUID-based and provider-independent.
Storage errors remain distinct from successful absence.

Before enabling B writes, deploy both selected bindings to all scheduling silos
with A as write provider and B as draining. Then roll out B as write provider
and A as draining. These settings take effect at startup. Pause scheduling for
a strict cutover boundary; otherwise A-configured silos can keep creating A
shards until the rollout and their admitted scheduling requests finish.

Keep A's identity authorized for listing, reads, conditional metadata updates,
journal writes, compaction, and deletes for the entire drain. These permissions
allow existing jobs to progress and their shards to be cleaned up.
Preserve WAL/checkpoint or Table partition mappings and the
readers needed to recover both providers.

Use `IDurableJobsStorageInspector.InspectAsync("jobs-a", cancellationToken)`
for a full `jobs/shards/` inventory, including future-dated and poisoned work.
Report inventory failures as unknown. After **all writers** have cut over,
require successful complete inventories with a total shard count of zero,
including unrecognized entries, before removing A. Retirement combines these
live observations with independent evidence of completed cluster-wide writer
cutover.

The [migration sample](../../../../samples/DurableJobsMigration/README.md)
uses disk-backed Azurite, two Blob namespaces, separate prepare/drain processes,
and local inventory reporting. See the
[core migration guidance](../../Orleans.DurableJobs/README.md#cut-over-and-retire-a-provider)
for rollback and retirement criteria.

#### Using Connection String
```csharp
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseAzureStorageClustering(options => options.ConfigureTableServiceClient("YOUR_STORAGE_ACCOUNT_URI"))
        .UseAzureBlobDurableJobs(options =>
        {
            options.BlobServiceClient = new BlobServiceClient("YOUR_AZURE_STORAGE_CONNECTION_STRING");
            options.ContainerName = "durable-jobs";
        });
});

await builder.Build().RunAsync();
```

#### Using Managed Identity (Recommended for Production)
```csharp
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseAzureStorageClustering(options => options.ConfigureTableServiceClient("YOUR_STORAGE_ACCOUNT_URI"))
        .UseAzureBlobDurableJobs(options =>
        {
            var credential = new DefaultAzureCredential();
            options.BlobServiceClient = new BlobServiceClient(
                new Uri("https://youraccount.blob.core.windows.net"),
                credential);
            options.ContainerName = "durable-jobs";
        });
});

await builder.Build().RunAsync();
```

#### With Advanced Options
```csharp
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .UseAzureStorageClustering(options => options.ConfigureTableServiceClient(connectionString))
        .UseAzureBlobDurableJobs(options =>
        {
            options.BlobServiceClient = new BlobServiceClient(connectionString);
            // Use different containers for different environments
            options.ContainerName = $"durable-jobs-{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")?.ToLowerInvariant()}";
        })
        .ConfigureServices(services =>
        {
            services.Configure<DurableJobsOptions>(options =>
            {
                // Shard duration: balance between latency and storage overhead
                options.ShardDuration = TimeSpan.FromMinutes(5);
                
                // Control concurrency to prevent overwhelming the system
                options.MaxConcurrentJobsPerSilo = 50;
                
                // Custom retry policy with exponential backoff
                options.ShouldRetry = (context, exception) =>
                {
                    // Don't retry on permanent failures
                    if (exception is ArgumentException or InvalidOperationException)
                        return null;
                    
                    // Exponential backoff with max 3 retries
                    if (context.DequeueCount < 3)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, context.DequeueCount));
                        return DateTimeOffset.UtcNow.Add(delay);
                    }
                    
                    return null;
                };
            });
        });
});
```

## Usage Example

### Email Scheduling with Cancellation
```csharp
using Orleans;
using Orleans.DurableJobs;

public interface IEmailGrain : IGrainWithStringKey
{
    Task ScheduleEmail(string subject, string body, DateTimeOffset sendTime);
    Task CancelScheduledEmail(CancellationToken requestCancellationToken);
}

public class EmailGrain : Grain, IEmailGrain, IDurableJobHandler
{
    private readonly ILocalDurableJobManager _jobManager;
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailGrain> _logger;
    private DurableJob? _durableEmailJob;

    public EmailGrain(
        ILocalDurableJobManager jobManager,
        IEmailService emailService,
        ILogger<EmailGrain> logger)
    {
        _jobManager = jobManager;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task ScheduleEmail(string subject, string body, DateTimeOffset sendTime)
    {
        var emailAddress = this.GetPrimaryKeyString();
        var metadata = new Dictionary<string, string>
        {
            ["Subject"] = subject,
            ["Body"] = body
        };

        _durableEmailJob = await _jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = this.GetGrainId(),
                JobName = "SendEmail",
                DueTime = sendTime,
                Metadata = metadata
            },
            CancellationToken.None);

        _logger.LogInformation(
            "Scheduled email to {EmailAddress} for {SendTime} (JobId: {JobId})",
            emailAddress, sendTime, _durableEmailJob.Id);
    }

    public async Task CancelScheduledEmail(CancellationToken requestCancellationToken)
    {
        if (_durableEmailJob is null)
        {
            _logger.LogWarning("No scheduled email to cancel");
            return;
        }

        var cancellationRequested = await _jobManager.CancelAsync(_durableEmailJob, requestCancellationToken);
        if (cancellationRequested)
        {
            _logger.LogInformation(
                "Email job {JobId} cancellation request recorded; no future attempt will start",
                _durableEmailJob.Id);
            // An already-running attempt may still complete.
            _durableEmailJob = null;
        }
        else
        {
            _logger.LogWarning(
                "Cancellation request was not recorded for email job {JobId} (it may have already completed)",
                _durableEmailJob.Id);
        }
    }

    public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        var emailAddress = this.GetPrimaryKeyString();
        var subject = context.Job.Metadata?["Subject"];
        var body = context.Job.Metadata?["Body"];

        _logger.LogInformation(
            "Sending email to {EmailAddress} (Job: {JobId}, Attempt: {Attempt})",
            emailAddress, context.Job.Id, context.DequeueCount);

        try
        {
            await _emailService.SendEmailAsync(emailAddress, subject, body, attemptCancellationToken);
            _logger.LogInformation("Email sent successfully to {EmailAddress}", emailAddress);
            _durableEmailJob = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {EmailAddress}", emailAddress);
            throw; // Let the retry policy handle it
        }
    }
}
```

### Order Workflow with Multiple Scheduled Steps
```csharp
public interface IOrderGrain : IGrainWithGuidKey
{
    Task PlaceOrder(OrderDetails order);
    Task CancelOrder();
}

public class OrderGrain : Grain, IOrderGrain, IDurableJobHandler
{
    private readonly ILocalDurableJobManager _jobManager;
    private readonly IOrderService _orderService;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<OrderGrain> _logger;
    private OrderDetails? _orderDetails;

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

    public async Task PlaceOrder(OrderDetails order)
    {
        _orderDetails = order;
        var orderId = this.GetPrimaryKey();

        // Create the order
        await _orderService.CreateOrderAsync(orderId, order);
        _logger.LogInformation("Order {OrderId} created for customer {CustomerId}", orderId, order.CustomerId);

        // Schedule payment reminder after 1 hour
        var paymentReminderTime = DateTimeOffset.UtcNow.AddHours(1);
        await _jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = this.GetGrainId(),
                JobName = "PaymentReminder",
                DueTime = paymentReminderTime,
                Metadata = new Dictionary<string, string>
                {
                    ["Step"] = "PaymentReminder",
                    ["CustomerEmail"] = order.CustomerEmail
                }
            },
            CancellationToken.None);

        // Schedule order expiration after 24 hours
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

        _logger.LogInformation(
            "Scheduled payment reminder for {ReminderTime} and expiration for {ExpirationTime}",
            paymentReminderTime, expirationTime);
    }

    public async Task CancelOrder()
    {
        var orderId = this.GetPrimaryKey();
        await _orderService.CancelOrderAsync(orderId);
        _orderDetails = null;
        _logger.LogInformation("Order {OrderId} canceled", orderId);
    }

    public async Task ExecuteJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        var step = context.Job.Metadata!["Step"];
        var orderId = this.GetPrimaryKey();

        _logger.LogInformation(
            "Executing workflow step {Step} for order {OrderId} (Attempt: {Attempt})",
            step, orderId, context.DequeueCount);

        switch (step)
        {
            case "PaymentReminder":
                await HandlePaymentReminder(context, attemptCancellationToken);
                break;

            case "OrderExpiration":
                await HandleOrderExpiration(attemptCancellationToken);
                break;

            default:
                _logger.LogWarning("Unknown workflow step: {Step}", step);
                break;
        }
    }

    private async Task HandlePaymentReminder(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        var orderId = this.GetPrimaryKey();
        var order = await _orderService.GetOrderAsync(orderId, attemptCancellationToken);
        
        if (order?.Status == OrderStatus.Pending)
        {
            var customerEmail = context.Job.Metadata!["CustomerEmail"];
            var emailGrain = _grainFactory.GetGrain<IEmailGrain>(customerEmail);
            
            await emailGrain.ScheduleEmail(
                "Payment Reminder",
                $"Your order {orderId} is awaiting payment. Please complete your purchase within 23 hours.",
                DateTimeOffset.UtcNow);

            _logger.LogInformation("Payment reminder sent for order {OrderId}", orderId);
        }
        else
        {
            _logger.LogInformation(
                "Skipping payment reminder for order {OrderId} - status is {Status}",
                orderId, order?.Status);
        }
    }

    private async Task HandleOrderExpiration(CancellationToken attemptCancellationToken)
    {
        var orderId = this.GetPrimaryKey();
        var order = await _orderService.GetOrderAsync(orderId, attemptCancellationToken);
        
        if (order?.Status == OrderStatus.Pending)
        {
            await _orderService.CancelOrderAsync(orderId, attemptCancellationToken);
            _logger.LogInformation("Order {OrderId} expired and canceled", orderId);

            // Notify customer
            var emailGrain = _grainFactory.GetGrain<IEmailGrain>(order.CustomerEmail);
            await emailGrain.ScheduleEmail(
                "Order Expired",
                $"Your order {orderId} has expired due to pending payment.",
                DateTimeOffset.UtcNow);
        }
        else
        {
            _logger.LogInformation(
                "Order {OrderId} did not expire - status is {Status}",
                orderId, order?.Status);
        }
    }
}

// Supporting types
public class OrderDetails
{
    public string CustomerId { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public decimal Amount { get; set; }
    public List<OrderItem> Items { get; set; } = new();
}

public enum OrderStatus
{
    Pending,
    Paid,
    Shipped,
    Delivered,
    Cancelled
}
```

## How It Works

### Storage Architecture
Each durable-job shard has a journal identity and persisted ownership metadata.
The journal-backed shard manager discovers shard identities through the storage
catalog and recovers state through the configured journal format.

The Blob provider stores the WAL at `wal/{journalId}` and checkpoints at
`checkpoints/{journalId}/{snapshotId}` within the configured container. The
Table provider stores journal headers and data generations in the configured
table. Provider metadata selects the committed checkpoint or generation; caller
metadata carries the durable-job shard's discovery and ownership properties.

### Shard Ownership and High Availability
1. **Optimistic Concurrency**: ETags prevent conflicting updates when multiple silos try to claim a shard
2. **Ownership Transfer**: When a silo fails, other silos detect the failure and claim orphaned shards
3. **Creator Priority**: The silo that created a shard gets priority to reclaim it if it loses ownership
4. **Automatic Cleanup**: Empty shards are deleted automatically after processing

### Job Lifecycle with Azure Storage
```
┌─────────────────────┐
│  Job Scheduled      │ ──▶ Committed to the shard journal
└─────────────────────┘
         │
         ▼
┌─────────────────────┐
│  Waiting in Shard   │ ──▶ Persisted in Azure journal storage
└─────────────────────┘
         │
         ▼
┌─────────────────────┐
│  Shard Owned        │ ──▶ Silo acquires ownership via metadata update
└─────────────────────┘
         │
         ▼
┌─────────────────────┐
│  Job Executed       │ ──▶ Handler invoked on target grain
└─────────────────────┘
         │
         ├──▶ Success ──▶ Completion persisted in the journal
         │
         └──▶ Failure ──▶ Retry: Updated due time in the journal
                          No Retry: Job entry removed
```

## Performance Considerations

### Concurrency Settings
```csharp
services.Configure<DurableJobsOptions>(options =>
{
    // Adjust based on your workload and Azure Storage limits
    options.MaxConcurrentJobsPerSilo = 50;
});
```

### Storage Costs
Account for stored WAL/checkpoint bytes or Table generations, append batches,
recovery reads, checkpoint publication and cleanup, ownership metadata updates,
and catalog listing pages. Standard and premium Blob use the same provider
operations with different account pricing and service characteristics.

Use `orleans-journaling-provider-catalog-pages`, `catalog-items`, and
`catalog-entries` (with the same prefix) to compare catalog traversal and
delivered results. `orleans-journaling-provider-retries` counts explicit
provider retries. The benchmarks pair these counters with independently
measured operation counts, outcomes, payload bytes, and latency.

Configure request counts, timing, and outcomes through host-owned Azure SDK
diagnostics, Aspire integrations, or other application instrumentation.
Reconcile transaction cost with Azure service metrics and the SDK's retry and
upload behavior.

## Monitoring and Troubleshooting

### Enable Logging
```csharp
builder.Logging.AddFilter("Orleans.DurableJobs", LogLevel.Information);
builder.Logging.AddFilter("Orleans.DurableJobs.AzureStorage", LogLevel.Information);
```

### Key Metrics to Monitor
- **Shard Assignment Time**: Time to claim ownership of unassigned shards
- **Job Execution Latency**: Time between due time and actual execution
- **Retry Rate**: Percentage of jobs requiring retry
- **Blob Operations**: Number of read/write/delete operations per minute

## Security Best Practices

### Use Managed Identity
```csharp
var credential = new DefaultAzureCredential();
var blobServiceClient = new BlobServiceClient(storageAccountUri, credential);
```

### Network Security
- Enable firewall rules to restrict access
- Use private endpoints for enhanced security
- Consider Azure Virtual Network integration

### Access Control
```csharp
// Minimum required permissions:
// - Storage Blob Data Contributor (for read/write/delete operations)
// - Or custom role with:
//   - Microsoft.Storage/storageAccounts/blobServices/containers/read
//   - Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read
//   - Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write
//   - Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete
```

## Documentation
For more comprehensive documentation, please refer to:
- [Microsoft Orleans Documentation](https://dotnet.github.io/orleans/docs/)
- [Azure Blob Storage Documentation](https://learn.microsoft.com/azure/storage/blobs/)
- [Orleans Durable Jobs Core Package](../../Orleans.DurableJobs/README.md)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
