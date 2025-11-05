using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableJobs;

/// <summary>
/// Provides contextual information about a durable job execution.
/// </summary>
public interface IDurableJobContext
{
    /// <summary>
    /// Gets the durable job being executed.
    /// </summary>
    DurableJob Job { get; }

    /// <summary>
    /// Gets the unique identifier for this execution run.
    /// </summary>
    string RunId { get; }

    /// <summary>
    /// Gets the number of times this job has been dequeued for execution, including retries.
    /// </summary>
    int DequeueCount { get; }
}

/// <summary>
/// Represents the execution context for a durable job.
/// </summary>
[GenerateSerializer]
internal class DurableJobContext : IDurableJobContext
{
    /// <summary>
    /// Gets the durable job being executed.
    /// </summary>
    [Id(0)]
    public DurableJob Job { get; }

    /// <summary>
    /// Gets the unique identifier for this execution run.
    /// </summary>
    [Id(1)]
    public string RunId { get; }

    /// <summary>
    /// Gets the number of times this job has been dequeued for execution, including retries.
    /// </summary>
    [Id(2)]
    public int DequeueCount { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DurableJobContext"/> class.
    /// </summary>
    /// <param name="job">The durable job to execute.</param>
    /// <param name="runId">The unique identifier for this execution run.</param>
    /// <param name="retryCount">The number of times this job has been dequeued, including retries.</param>
    public DurableJobContext(DurableJob job, string runId, int retryCount)
    {
        Job = job;
        RunId = runId;
        DequeueCount = retryCount;
    }
}

/// <summary>
/// Defines the interface for handling durable job execution.
/// Grains implement this interface to receive and process durable jobs.
/// </summary>
/// <remarks>
/// <para>
/// Grains that implement this interface can be targeted by durable jobs.
/// The <see cref="ExecuteJobAsync"/> method is invoked when the job's due time is reached.
/// </para>
/// <example>
/// The following example demonstrates a grain that implements <see cref="IDurableJobHandler"/>:
/// <code>
/// public class MyGrain : Grain, IDurableJobHandler
/// {
///     public Task ExecuteJobAsync(IDurableJobContext context, CancellationToken cancellationToken)
///     {
///         // Process the durable job
///         var jobName = context.Job.Name;
///         var dueTime = context.Job.DueTime;
///         
///         // Perform job logic here
///         
///         return Task.CompletedTask;
///     }
/// }
/// </code>
/// </example>
/// </remarks>
public interface IDurableJobHandler
{
    /// <summary>
    /// Executes the durable job with the provided context.
    /// </summary>
    /// <param name="context">The context containing information about the durable job execution.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous job execution operation.</returns>
    /// <remarks>
    /// <para>
    /// This method is invoked by the Orleans durable jobs infrastructure when a job's due time is reached.
    /// Implementations should handle job execution logic and can use information from the <paramref name="context"/>
    /// to access job metadata, dequeue count for retry logic, and other execution details.
    /// </para>
    /// <para>
    /// If the method throws an exception and a retry policy is configured, the job may be retried.
    /// The <see cref="IDurableJobContext.DequeueCount"/> property can be used to determine if this is a retry attempt.
    /// </para>
    /// </remarks>
    Task ExecuteJobAsync(IDurableJobContext context, CancellationToken cancellationToken);
}
