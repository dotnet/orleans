# Microsoft Orleans Durable Jobs for Azure Storage

## Introduction
Microsoft Orleans Durable Jobs for Azure Storage provides persistent storage for Orleans Durable Jobs using Azure Blob Storage. This allows your Orleans applications to schedule jobs that survive silo restarts, grain deactivation, and cluster reconfigurations. Jobs are stored in append blobs, providing efficient storage and retrieval for time-based job scheduling.

## Getting Started

### Installation
To use this package, install it via NuGet along with the core package:

```shell
dotnet add package Microsoft.Orleans.DurableJobs
dotnet add package Microsoft.Orleans.DurableJobs.AzureStorage
```

### Configuration

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
        .UseAzureStorageDurableJobs(options =>
        {
            options.Configure(o =>
            {
                o.BlobServiceClient = new BlobServiceClient("YOUR_AZURE_STORAGE_CONNECTION_STRING");
                o.ContainerName = "durable-jobs";
            });
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
        .UseAzureStorageDurableJobs(options =>
        {
            options.Configure(o =>
            {
                var credential = new DefaultAzureCredential();
                o.BlobServiceClient = new BlobServiceClient(
                    new Uri("https://youraccount.blob.core.windows.net"),
                    credential);
                o.ContainerName = "durable-jobs";
            });
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
        .UseAzureStorageDurableJobs(options =>
        {
            options.Configure(o =>
            {
                o.BlobServiceClient = new BlobServiceClient(connectionString);
                // Use different containers for different environments
                o.ContainerName = $"durable-jobs-{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")?.ToLowerInvariant()}";
            });
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
    Task CancelScheduledEmail();
}

public class EmailGrain : Grain, IEmailGrain, IDurableJobHandler
{
    private readonly ILocalDurableJobManager _jobManager;
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailGrain> _logger;
    private IDurableJob? _durableEmailJob;

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
            this.GetGrainId(),
            "SendEmail",
            sendTime,
            metadata);

        _logger.LogInformation(
            "Scheduled email to {EmailAddress} for {SendTime} (JobId: {JobId})",
            emailAddress, sendTime, _durableEmailJob.Id);
    }

    public async Task CancelScheduledEmail()
    {
        if (_durableEmailJob is null)
        {
            _logger.LogWarning("No scheduled email to cancel");
            return;
        }

        var canceled = await _jobManager.TryCancelDurableJobAsync(_durableEmailJob);
        if (canceled)
        {
            _logger.LogInformation("Email job {JobId} canceled successfully", _durableEmailJob.Id);
            _durableEmailJob = null;
        }
        else
        {
            _logger.LogWarning("Failed to cancel email job {JobId} (may have already executed)", _durableEmailJob.Id);
        }
    }

    public async Task ExecuteJobAsync(IDurableJobContext context, CancellationToken cancellationToken)
    {
        var emailAddress = this.GetPrimaryKeyString();
        var subject = context.Job.Metadata?["Subject"];
        var body = context.Job.Metadata?["Body"];

        _logger.LogInformation(
            "Sending email to {EmailAddress} (Job: {JobId}, Attempt: {Attempt})",
            emailAddress, context.Job.Id, context.DequeueCount);

        try
        {
            await _emailService.SendEmailAsync(emailAddress, subject, body, cancellationToken);
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
            this.GetGrainId(),
            "PaymentReminder",
            paymentReminderTime,
            new Dictionary<string, string>
            {
                ["Step"] = "PaymentReminder",
                ["CustomerEmail"] = order.CustomerEmail
            });

        // Schedule order expiration after 24 hours
        var expirationTime = DateTimeOffset.UtcNow.AddHours(24);
        await _jobManager.ScheduleJobAsync(
            this.GetGrainId(),
            "OrderExpiration",
            expirationTime,
            new Dictionary<string, string>
            {
                ["Step"] = "OrderExpiration"
            });

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

    public async Task ExecuteJobAsync(IDurableJobContext context, CancellationToken cancellationToken)
    {
        var step = context.Job.Metadata!["Step"];
        var orderId = this.GetPrimaryKey();

        _logger.LogInformation(
            "Executing workflow step {Step} for order {OrderId} (Attempt: {Attempt})",
            step, orderId, context.DequeueCount);

        switch (step)
        {
            case "PaymentReminder":
                await HandlePaymentReminder(context, cancellationToken);
                break;

            case "OrderExpiration":
                await HandleOrderExpiration(cancellationToken);
                break;

            default:
                _logger.LogWarning("Unknown workflow step: {Step}", step);
                break;
        }
    }

    private async Task HandlePaymentReminder(IDurableJobContext context, CancellationToken ct)
    {
        var orderId = this.GetPrimaryKey();
        var order = await _orderService.GetOrderAsync(orderId, ct);
        
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

    private async Task HandleOrderExpiration(CancellationToken ct)
    {
        var orderId = this.GetPrimaryKey();
        var order = await _orderService.GetOrderAsync(orderId, ct);
        
        if (order?.Status == OrderStatus.Pending)
        {
            await _orderService.CancelOrderAsync(orderId, ct);
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
1. **Blob Container**: All jobs are stored in a single Azure Blob Storage container
2. **Append Blobs**: Each job shard is stored as an append blob, providing efficient sequential writes
3. **Blob Naming**: Blobs are named with the pattern: `{ShardStartTime:yyyyMMddHHmm}-{SiloAddress}-{Index}`
4. **Metadata**: Blob metadata stores ownership and time range information:
   - `Owner`: The silo currently processing this shard
   - `Creator`: The silo that created this shard
   - `MinDueTime`: Start of the time range for jobs in this shard
   - `MaxDueTime`: End of the time range for jobs in this shard

### Shard Ownership and High Availability
1. **Optimistic Concurrency**: ETags prevent conflicting updates when multiple silos try to claim a shard
2. **Ownership Transfer**: When a silo fails, other silos detect the failure and claim orphaned shards
3. **Creator Priority**: The silo that created a shard gets priority to reclaim it if it loses ownership
4. **Automatic Cleanup**: Empty shards are deleted automatically after processing

### Job Lifecycle with Azure Storage
```
┌─────────────────────┐
│  Job Scheduled      │ ──▶ Written to append blob
└─────────────────────┘
         │
         ▼
┌─────────────────────┐
│  Waiting in Shard   │ ──▶ Persisted in Azure Blob Storage
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
         ├──▶ Success ──▶ Job entry removed from blob
         │
         └──▶ Failure ──▶ Retry: Updated due time in blob
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
- **Container**: One container per cluster
- **Blobs**: One blob per active time shard
- **Operations**: 
  - Schedule job: 1-2 append operations
  - Execute job: 1 read + 1 delete operation
  - Shard ownership transfer: 1 metadata update

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
- [Microsoft Orleans Documentation](https://learn.microsoft.com/dotnet/orleans/)
- [Azure Blob Storage Documentation](https://learn.microsoft.com/azure/storage/blobs/)
- [Orleans Durable Jobs Core Package](../../../Orleans.DurableJobs/README.md)

## Feedback & Contributing
- If you have any issues or would like to provide feedback, please [open an issue on GitHub](https://github.com/dotnet/orleans/issues)
- Join our community on [Discord](https://aka.ms/orleans-discord)
- Follow the [@msftorleans](https://twitter.com/msftorleans) Twitter account for Orleans announcements
- Contributions are welcome! Please review our [contribution guidelines](https://github.com/dotnet/orleans/blob/main/CONTRIBUTING.md)
- This project is licensed under the [MIT license](https://github.com/dotnet/orleans/blob/main/LICENSE)
