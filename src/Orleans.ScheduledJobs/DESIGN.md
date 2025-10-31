# Orleans Scheduled Jobs - Design Document

## Executive Summary

Orleans Scheduled Jobs is a new distributed, scalable system for scheduling one-time jobs that execute at specific times within Microsoft Orleans clusters. Unlike Orleans Reminders (designed for recurring tasks), Scheduled Jobs targets one-time future events such as appointment notifications, delayed processing, scheduled workflow steps, and time-based triggers.

**Key Capabilities:**
- One-time execution at specified times
- Persistence across grain deactivations and silo restarts
- Automatic distribution and rebalancing across cluster nodes
- Configurable retry policies for failed jobs
- Rich metadata support for job context
- Job cancellation before execution
- Multiple storage backends (in-memory for dev/test, Azure Blob Storage for production)

## Motivation

### The Problem

Orleans Reminders have several well-documented issues that affect scalability, reliability, and performance.

Memory constraints limit scalability because Orleans loads all reminders into memory at silo startup, partitioning them across the cluster regardless of when they're scheduled to fire. This creates a hard ceiling on the total number of reminders based on available cluster memory, even though reminders scale horizontally across silos.

Additionally, if the cluster is down when a reminder tick is due, that specific occurrence is missed entirely. 

### Solution Requirements

1. **One-time execution semantics** - Jobs execute once, not repeatedly
2. **Persistence** - Survive grain deactivation and silo failures
3. **Distribution** - Automatic load balancing across silos
4. **Reliability** - Survive silo crashes with automatic failover
5. **Scalability** - Support thousands of concurrent jobs per silo
6. **Flexibility** - Configurable retry policies and metadata support
7. **Simplicity** - Easy to use with minimal configuration

## Architecture

### High-Level Design

```
┌────────────────────────────────────────────────────────────────┐
│                        Orleans Cluster                         │
│                                                                │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │                       Silo 1                             │  │
│  │  ┌────────────────────────────────────────────────────┐  │  │
│  │  │   LocalScheduledJobManager (SystemTarget)          │  │  │
│  │  │   • Monitors cluster membership                    │  │  │
│  │  │   • Claims orphaned shards                         │  │  │
│  │  │   • Processes owned job shards                     │  │  │
│  │  │   • Delivers jobs to target grains                 │  │  │
│  │  └────────────────────────────────────────────────────┘  │  │
│  │                           │                              │  │
│  │                           ▼                              │  │
│  │  ┌────────────────────────────────────────────────────┐  │  │
│  │  │    JobShard (Time-based partition)                 │  │  │
│  │  │    StartTime: 2024-01-15 10:00:00                  │  │  │
│  │  │    EndTime:   2024-01-15 11:00:00                  │  │  │
│  │  │    Contains: InMemoryJobQueue                      │  │  │
│  │  └────────────────────────────────────────────────────┘  │  │
│  │                                                          │  │
│  │  ┌────────────────────────────────────────────────────┐  │  │
│  │  │         Grains (Job Handlers)                      │  │  │
│  │  │    • Implement IScheduledJobHandler                │  │  │
│  │  │    • Receive job execution callbacks               │  │  │
│  │  └────────────────────────────────────────────────────┘  │  │
│  └──────────────────────────────────────────────────────────┘  │
│                                                                │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │               Silo 2, 3, ... (Similar)                   │  │
│  └──────────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────────┘
                               │
                               ▼
              ┌────────────────────────────────┐
              │   JobShardManager (Storage)    │
              │   • In-Memory (dev/testing)    │
              │   • Azure Blob (production)    │
              │   • Tracks shard ownership     │
              └────────────────────────────────┘
```

### Core Components

#### 1. IScheduledJobHandler

**Purpose**: Interface that grains implement to receive scheduled job callbacks.

**API:**
```csharp
public interface IScheduledJobHandler
{
    Task ExecuteJobAsync(IScheduledJobContext context, CancellationToken cancellationToken);
}

public interface IScheduledJobContext
{
    IScheduledJob Job { get; }           // Job details
    string RunId { get; }                // Unique execution ID
    int DequeueCount { get; }            // Number of attempts (for retry logic)
}
```

**Responsibilities:**
- Defines contract for job execution
- Provides context including metadata, retry count, execution ID
- Enables grains to implement custom job processing logic

#### 2. LocalScheduledJobManager

**Type**: SystemTarget (one per silo)

**Purpose**: Orchestrates job scheduling, shard ownership, and job execution on the local silo.

**Key Responsibilities:**
- **Job Scheduling**: Accept scheduling requests and route to appropriate shards
- **Cluster Monitoring**: Listen for membership changes via `IClusterMembershipService`
- **Shard Assignment**: Claim orphaned shards when silos fail
- **Job Execution**: Dequeue jobs from owned shards and invoke target grains
- **Concurrency Control**: Limit concurrent executions via semaphore
- **Retry Management**: Apply retry policies to failed jobs

**Key Methods:**
```csharp
Task<IScheduledJob> ScheduleJobAsync(
    GrainId target, 
    string jobName, 
    DateTimeOffset dueTime, 
    IReadOnlyDictionary<string, string>? metadata = null);

Task<bool> TryCancelScheduledJobAsync(IScheduledJob job);
```

**Lifecycle:**
- Starts during `ServiceLifecycleStage.Active`
- Spawns background tasks for membership monitoring and shard processing
- Stops gracefully, waiting for running jobs to complete

#### 3. JobShard

**Purpose**: Represents a time-based partition of scheduled jobs.

**Properties:**
- `Id`: Unique identifier
- `StartTime`: Minimum due time for jobs in this shard
- `EndTime`: Maximum due time for jobs in this shard
- `IsComplete`: Whether the shard can take new jobs
- `Metadata`: Custom shard metadata

**Key Operations:**
```csharp
Task<IScheduledJob> ScheduleJobAsync(...);
IAsyncEnumerable<IScheduledJobContext> ConsumeScheduledJobsAsync();
Task RemoveJobAsync(string jobId);
Task RetryJobLaterAsync(IScheduledJobContext context, DateTimeOffset newDueTime);
Task MarkAsComplete();
```

**Implementations:**
- **InMemoryJobShard**: Non-persistent, for development/testing
- **AzureStorageJobShard**: Persistent, backed by Azure Append Blobs

#### 4. InMemoryJobQueue

**Purpose**: Priority queue that manages job ordering by due time.

**Features:**
- **Time-bucketing**: Groups jobs by second-precision for efficiency
- **Lazy dequeue**: Jobs dequeued only when due
- **O(1) cancellation**: Fast removal by job ID using dictionary lookup
- **Retry support**: Re-enqueue jobs with updated due times
- **Async enumeration**: Stream jobs as they become ready

**Algorithm:**
- Uses `PriorityQueue<JobBucket, DateTimeOffset>` for bucket ordering
- Each bucket contains jobs due within the same second
- Polls every 1 second, dequeuing ready jobs

#### 5. JobShardManager

**Purpose**: Abstract base class for managing shard lifecycle and ownership.

**Key Operations:**
```csharp
Task<List<JobShard>> AssignJobShardsAsync(SiloAddress silo, DateTimeOffset maxDueTime);
Task<JobShard> RegisterShard(SiloAddress silo, DateTimeOffset minDueTime, DateTimeOffset maxDueTime, ...);
Task UnregisterShard(SiloAddress silo, JobShard shard);
```

**Implementations:**
- **InMemoryJobShardManager**: Simple in-memory storage
- **AzureStorageJobShardManager**: Durable Azure Blob Storage with optimistic concurrency

### Time-Based Sharding Strategy

**Rationale:**
- Enables parallel processing across silos
- Facilitates automatic failover (entire shards reassigned)
- Allows progressive cleanup of completed shards
- Bounds batch size per shard

**Shard Key Calculation:**
```csharp
private static DateTimeOffset GetShardKey(DateTimeOffset scheduledTime)
{
    // Minute-level precision
    return new DateTime(
        scheduledTime.Year, 
        scheduledTime.Month, 
        scheduledTime.Day, 
        scheduledTime.Hour, 
        scheduledTime.Minute, 
        0);
}
```

**Default Shard Duration**: 1 hour (configurable via `ScheduledJobsOptions.ShardDuration`)

**Assignment Logic:**
1. When scheduling a job, find/create a shard covering the due time
2. If shard start time is within 5 minutes, assign to current silo
3. Otherwise, leave unassigned for later assignment
4. On silo failure, orphaned shards are claimed by healthy silos

## Storage Backends

### In-Memory Storage

**Use Case**: Development, testing, scenarios without persistence requirements

**Implementation**: `InMemoryJobShardManager`

**Characteristics:**
- Dictionary-based storage
- No external dependencies
- Fast and lightweight
- Data lost on silo restart

**Configuration:**
```csharp
siloBuilder.UseInMemoryScheduledJobs();
```

### Azure Blob Storage

**Use Case**: Production deployments requiring persistence and high availability

**Implementation**: `AzureStorageJobShardManager`, `AzureStorageJobShard`

**Storage Model:**
- **Container**: One per Orleans cluster (e.g., `"scheduled-jobs"`)
- **Blob**: One append blob per shard
- **Blob Name**: `{MinDueTime:yyyyMMddHHmm}-{SiloAddress}-{Index}`
- **Blob Metadata**: `Owner`, `Creator`, `MinDueTime`, `MaxDueTime`

**Operations Log Format:**
Each blob is an append-only log of JSON operations:

```json
{"Type":"Add","Id":"job-123","Name":"SendReminder","DueTime":"2024-01-15T10:30:00Z","TargetGrainId":"...","Metadata":{...}}
{"Type":"Remove","Id":"job-123"}
{"Type":"Retry","Id":"job-456","DueTime":"2024-01-15T10:35:00Z"}
```

**State Reconstruction:**
When taking over a shard:
1. Download entire blob content
2. Replay operations sequentially to rebuild state
3. Load jobs into in-memory priority queue
4. Begin processing

**Concurrency Control:**
- Uses Azure Blob ETags for optimistic concurrency
- Each operation updates blob's ETag
- Ownership changes use metadata updates with ETag conditions
- Conflicting writes fail with HTTP 412 (Precondition Failed)

**Configuration:**
```csharp
siloBuilder.UseAzureStorageScheduledJobs(options =>
{
    options.Configure(o =>
    {
        o.BlobServiceClient = new BlobServiceClient(connectionString);
        o.ContainerName = "scheduled-jobs";
    });
});
```

## Job Lifecycle

### State Diagram

```
┌─────────────┐
│  Scheduled  │ ◄─── Grain calls ScheduleJobAsync()
└─────┬───────┘      • Job persisted to storage
      │              • Added to shard
      │
      ▼
┌─────────────┐
│   Pending   │ ◄─── Job waits in priority queue
└─────┬───────┘      • Ordered by due time
      │              • Can be canceled
      │
      ▼ (due time reached)
┌─────────────┐
│  Dequeued   │ ◄─── LocalScheduledJobManager dequeues job
└─────┬───────┘      • Acquired concurrency slot
      │              • Incremented DequeueCount
      │
      ▼
┌─────────────┐
│  Executing  │ ◄─── Target grain's ExecuteJobAsync() invoked
└─────┬───────┘
      │
      ├──▶ Success ──▶ RemoveJobAsync() ──▶ [Deleted from storage]
      │
      └──▶ Failure ──▶ ShouldRetry(context, exception)?
                        │
                        ├──▶ Yes ──▶ RetryJobLaterAsync() ──▶ [Back to Pending]
                        │            • Update due time
                        │            • Persist retry operation
                        │
                        └──▶ No ──▶ RemoveJobAsync() ──▶ [Deleted from storage]
```

### Detailed Flow

1. **Scheduling Phase**:
   - Grain calls `ILocalScheduledJobManager.ScheduleJobAsync()`
   - Manager calculates shard key from due time
   - Checks shard cache for existing shard
   - If no shard exists, creates new shard (may assign to self)
   - Job persisted to storage (Azure Blob append operation)
   - Job added to in-memory priority queue
   - Returns `IScheduledJob` handle to caller

2. **Waiting Phase**:
   - Job remains in priority queue until due time
   - Queue polls every 1 second for ready jobs
   - Job can be canceled via `TryCancelScheduledJobAsync()`

3. **Execution Phase**:
   - Job dequeued when due time reached
   - Concurrency semaphore acquired (`MaxConcurrentJobsPerSilo`)
   - Target grain located via `IGrainFactory`
   - `IScheduledJobReceiverExtension.DeliverScheduledJobAsync()` invoked
   - Extension calls grain's `ExecuteJobAsync()` implementation

4. **Completion Phase**:
   - **Success**: Job removed from storage and shard
   - **Failure**: Retry policy consulted
     - If retry: Update due time, persist retry operation, re-enqueue
     - If no retry: Remove from storage
   - Concurrency semaphore released

## Configuration

### ScheduledJobsOptions

```csharp
public sealed class ScheduledJobsOptions
{
    // Duration of each time shard (default: 1 hour)
    public TimeSpan ShardDuration { get; set; } = TimeSpan.FromHours(1);
    
    // Max concurrent job executions per silo (default: 10)
    public int MaxConcurrentJobsPerSilo { get; set; } = 10;
    
    // Retry policy (default: 5 retries with exponential backoff)
    public Func<IScheduledJobContext, Exception, DateTimeOffset?> ShouldRetry { get; set; }
}
```

### Configuration Examples

**Basic Configuration:**
```csharp
siloBuilder.UseInMemoryScheduledJobs();
```

**Production Configuration:**
```csharp
siloBuilder.UseAzureStorageScheduledJobs(options =>
{
    options.Configure(o =>
    {
        o.BlobServiceClient = new BlobServiceClient(connectionString);
        o.ContainerName = "scheduled-jobs";
    });
})
.ConfigureServices(services =>
{
    services.Configure<ScheduledJobsOptions>(options =>
    {
        options.ShardDuration = TimeSpan.FromMinutes(30);
        options.MaxConcurrentJobsPerSilo = 100;
        options.ShouldRetry = (context, ex) =>
        {
            // Max 3 retries
            if (context.DequeueCount >= 3) return null;
            
            // Only retry transient errors
            if (ex is HttpRequestException or TimeoutException)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, context.DequeueCount));
                return DateTimeOffset.UtcNow.Add(delay);
            }
            
            return null; // Don't retry permanent errors
        };
    });
});
```

## Fault Tolerance

### Silo Failure Handling

**Scenario**: A silo crashes while processing shards

**Recovery Process:**
1. Cluster membership service detects failure (5-30 seconds)
2. All active silos receive membership update
3. Each silo queries storage for orphaned shards (owner is dead)
4. First silo to update blob metadata (with ETag check) takes ownership
5. New owner downloads blob, reconstructs state, resumes processing

**Key Properties:**
- **No distributed lock**: Optimistic concurrency prevents conflicts
- **Automatic failover**: No manual intervention required
- **Fast recovery**: Shards claimed within seconds
- **No data loss**: All operations persisted before acknowledgment

### Split-Brain Prevention

Azure Blob Storage's strong consistency + ETag-based concurrency prevents split-brain:
- Only one silo can own a shard at any time
- Metadata updates require matching ETag (compare-and-swap)
- Failed attempts immediately detected and logged

### Execution Guarantees

**At-Least-Once Semantics:**
- Jobs removed from storage AFTER successful execution
- Failures or crashes may cause duplicate execution
- Network partitions may cause redelivery

**Idempotency Requirement:**
Job handlers MUST be idempotent. Example:

```csharp
public async Task ExecuteJobAsync(IScheduledJobContext context, CancellationToken ct)
{
    var jobId = context.Job.Id;
    
    // Check if already processed (using grain state, database, etc.)
    if (await _state.IsProcessedAsync(jobId))
    {
        _logger.LogInformation("Job {JobId} already processed", jobId);
        return;
    }
    
    // Process job logic
    await ProcessJobLogic(context.Job);
    
    // Mark as processed
    await _state.MarkProcessedAsync(jobId);
}
```

## Performance

### Scalability

**Horizontal Scaling:**
- Linear scaling with number of silos
- Each silo processes independent shards
- No cross-silo coordination for execution

**Vertical Scaling:**
- Controlled by `MaxConcurrentJobsPerSilo`
- Default: 10 concurrent jobs per silo
- Adjust based on workload and resources

**Throughput:**
- **In-Memory**: Thousands of jobs/second per silo
- **Azure Storage**: ~100-500 operations/second per shard (blob append limit)

### Latency

| Operation | In-Memory | Azure Storage |
|-----------|-----------|---------------|
| Job Scheduling | < 1ms | 10-50ms |
| Execution Latency | 1-2 seconds (polling interval) | 1-2 seconds |
| Shard Failover | 5-35 seconds | 5-35 seconds |
| State Reconstruction | N/A | 100ms - 5 seconds |

**Execution Precision**: ~1 second (due to polling interval)

### Resource Usage

**Memory per Silo:**
- Per job: ~200-500 bytes
- Per shard: ~1KB + jobs
- Typical: 10-50 shards × 1000 jobs = 5-25MB

**Azure Storage:**
- Per job: ~500 bytes - 2KB (JSON)
- Operations per job: 2 (schedule + remove)
- Metadata updates: 1-2 per shard takeover

**CPU:**
- Minimal during steady state
- Polling: 1 check/second per active shard
- Burst during shard takeover (state reconstruction)

## API Design

### Scheduling Jobs

```csharp
public class NotificationGrain : Grain, IScheduledJobHandler
{
    private readonly ILocalScheduledJobManager _jobManager;

    public async Task ScheduleReminder(string message, DateTimeOffset sendTime)
    {
        var metadata = new Dictionary<string, string>
        {
            ["Message"] = message,
            ["Priority"] = "High"
        };

        var job = await _jobManager.ScheduleJobAsync(
            this.GetGrainId(),
            "SendReminder",
            sendTime,
            metadata);

        _logger.LogInformation("Scheduled job {JobId}", job.Id);
    }

    public Task ExecuteJobAsync(IScheduledJobContext context, CancellationToken ct)
    {
        var message = context.Job.Metadata?["Message"];
        _logger.LogInformation("Sending: {Message}", message);
        // Send notification...
        return Task.CompletedTask;
    }
}
```

### Canceling Jobs

```csharp
public async Task CancelReminder(IScheduledJob job)
{
    var canceled = await _jobManager.TryCancelScheduledJobAsync(job);
    _logger.LogInformation("Canceled: {Success}", canceled);
}
```

### Custom Retry Policy

```csharp
options.ShouldRetry = (context, exception) =>
{
    // Max 3 attempts
    if (context.DequeueCount >= 3) return null;
    
    // Only retry specific exceptions
    return exception switch
    {
        HttpRequestException => DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, context.DequeueCount)),
        TimeoutException => DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, context.DequeueCount)),
        _ => null // Don't retry
    };
};
```

## Future Enhancements

### Potential Additions

1. **Additional Storage Providers:**
   - Redis
   - Azure Cosmos DB
   - ADO.NET (SQL Server, PostgreSQL)
   - AWS DynamoDB

2. **Advanced Features:**
   - Job priorities within shards
   - Job dependencies (B runs after A)
   - Batch scheduling (atomic multi-job scheduling)
   - Execution history/audit log

3. **Observability:**
   - OpenTelemetry metrics
   - Distributed tracing
   - Health checks

4. **Performance:**
   - Configurable polling intervals
   - Batch blob operations
   - Dynamic shard splitting

5. **Operations:**
   - Admin API for job inspection
   - Management dashboard
   - Manual rebalancing

## Migration Guide

### For Existing Orleans Applications

**Step 1**: Install NuGet packages
```bash
dotnet add package Microsoft.Orleans.ScheduledJobs
dotnet add package Microsoft.Orleans.ScheduledJobs.AzureStorage  # For production
```

**Step 2**: Configure silo
```csharp
siloBuilder.UseAzureStorageScheduledJobs(options =>
{
    options.Configure(o =>
    {
        o.BlobServiceClient = new BlobServiceClient(connectionString);
        o.ContainerName = "scheduled-jobs";
    });
});
```

**Step 3**: Implement handler interface
```csharp
public class MyGrain : Grain, IScheduledJobHandler
{
    public Task ExecuteJobAsync(IScheduledJobContext context, CancellationToken ct)
    {
        // Handle job execution
        return Task.CompletedTask;
    }
}
```

**Step 4**: Schedule jobs
```csharp
await _jobManager.ScheduleJobAsync(grainId, "MyJob", dueTime);
```

## Security

### Azure Storage Security

**Required Permissions:**
- `Microsoft.Storage/storageAccounts/blobServices/containers/read`
- `Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read`
- `Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write`
- `Microsoft.Storage/storageAccounts/blobServices/containers/blobs/delete`

**Best Practices:**
- Use Azure Managed Identity for authentication
- Enable Storage Account firewall rules
- Use private endpoints for enhanced security
- Enable blob soft delete for recovery

### Job Metadata

**Considerations:**
- Metadata stored in plain text
- Avoid sensitive data (passwords, secrets, PII)
- Use Azure Storage encryption at rest
- Consider application-level encryption for sensitive metadata

## Conclusion

Orleans Scheduled Jobs provides a robust, production-ready solution for one-time job scheduling in distributed Orleans applications. The design emphasizes:

- **Simplicity**: Minimal configuration, intuitive API
- **Reliability**: Persistent storage, automatic failover, retry policies
- **Scalability**: Horizontal scaling, efficient resource usage
- **Flexibility**: Multiple storage backends, configurable options
- **Integration**: Seamless fit with Orleans grain model

The feature is currently implemented and tested, ready for production use.

## Appendix

### Key Files

**Core Implementation:**
- `src/Orleans.ScheduledJobs/LocalScheduledJobManager.cs`
- `src/Orleans.ScheduledJobs/JobShard.cs`
- `src/Orleans.ScheduledJobs/InMemoryJobQueue.cs`
- `src/Orleans.ScheduledJobs/IScheduledJobHandler.cs`
- `src/Orleans.ScheduledJobs/ScheduledJob.cs`

**Azure Storage Provider:**
- `src/Azure/Orleans.ScheduledJobs.AzureStorage/AzureStorageJobShardManager.cs`
- `src/Azure/Orleans.ScheduledJobs.AzureStorage/AzureStorageJobShard.cs`

**Configuration:**
- `src/Orleans.ScheduledJobs/Hosting/ScheduledJobsOptions.cs`
- `src/Orleans.ScheduledJobs/Hosting/ScheduledJobsExtensions.cs`

**Tests:**
- `test/DefaultCluster.Tests/ScheduledJobTests.cs`
- `test/Extensions/TesterAzureUtils/ScheduledJobs/AzureStorageJobShardManagerTests.cs`
- `test/NonSilo.Tests/ScheduledJobs/InMemoryJobQueueTests.cs`

### References

- **Repository**: https://github.com/dotnet/orleans
- **Branch**: wip/scheduled-jobs
- **Core Package**: `Microsoft.Orleans.ScheduledJobs`
- **Azure Provider**: `Microsoft.Orleans.ScheduledJobs.AzureStorage`
- **Documentation**: README.md files in respective package directories
