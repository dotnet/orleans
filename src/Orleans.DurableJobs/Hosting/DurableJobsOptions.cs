using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.DurableJobs;

namespace Orleans.Hosting;

/// <summary>
/// Configuration options for the durable jobs feature.
/// </summary>
public sealed class DurableJobsOptions
{
    /// <summary>
    /// Gets or sets the duration of each job shard. Smaller values reduce latency but increase overhead.
    /// For optimal alignment with hour boundaries, choose durations that evenly divide 60 minutes
    /// (e.g., 1, 2, 3, 4, 5, 6, 10, 12, 15, 20, 30, or 60 minutes) to avoid bucket drift across hours.
    /// Default: 1 hour.
    /// </summary>
    public TimeSpan ShardDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets or sets how far in advance (before the shard's start time) the shard should
    /// begin processing. This prevents holding idle shards for extended periods.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan ShardActivationBufferPeriod { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the maximum number of jobs that can be executed concurrently on a single silo.
    /// Default: 10,000 × processor count.
    /// </summary>
    public int MaxConcurrentJobsPerSilo { get; set; } = 10_000 * Environment.ProcessorCount;

    /// <summary>
    /// Gets or sets the delay between overload checks when the host is overloaded.
    /// Job batch processing will pause for this duration before rechecking the overload status.
    /// Default: 5 second.
    /// </summary>
    public TimeSpan OverloadBackoffDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets whether concurrent job slow start is enabled.
    /// When enabled, job concurrency is gradually increased during startup to avoid starvation
    /// issues that can occur before caches, connection pools, and thread pool sizing have warmed up.
    /// Concurrency starts at <see cref="SlowStartInitialConcurrency"/> and doubles every
    /// <see cref="SlowStartInterval"/> until <see cref="MaxConcurrentJobsPerSilo"/> is reached.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool ConcurrencySlowStartEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the initial number of concurrent jobs allowed per silo when slow start is enabled.
    /// Concurrency will exponentially increase from this value until <see cref="MaxConcurrentJobsPerSilo"/> is reached.
    /// Default: <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    public int SlowStartInitialConcurrency { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Gets or sets the interval at which concurrency is doubled during slow start ramp-up.
    /// Default: 10 seconds.
    /// </summary>
    public TimeSpan SlowStartInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the function that determines whether a failed job should be retried and when.
    /// The function receives the job context and the exception that caused the failure, and returns
    /// the time when the job should be retried, or <see langword="null"/> if the job should not be retried.
    /// Default: Retry up to 5 times with exponential backoff (2^n seconds).
    /// </summary>
    public Func<IJobRunContext, Exception, DateTimeOffset?> ShouldRetry { get; set; } = DefaultShouldRetry;

    /// <summary>
    /// Gets or sets the maximum number of times a shard can be adopted from a dead owner before
    /// being marked as poisoned. A shard that repeatedly causes silos to crash will exceed this
    /// threshold as it bounces between owners. When the next adoption would cause the adopted count
    /// to exceed this value, the shard is considered poisoned and will no longer be assigned to any silo.
    /// Default: 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The adopted count is only incremented when a shard is taken from a dead silo (i.e., the previous
    /// owner crashed). It is NOT incremented when a silo gracefully shuts down and releases ownership.
    /// </para>
    /// <para>
    /// When a shard completes successfully (all jobs processed), the adopted count is reset to 0.
    /// </para>
    /// </remarks>
    public int MaxAdoptedCount { get; set; } = 3;

    /// <summary>
    /// Gets or sets the maximum number of orphaned shards a silo may claim immediately
    /// after startup. The cumulative budget grows linearly from this value to
    /// <see cref="SlowStartMaxBudget"/> over <see cref="SlowStartRampUpDuration"/>,
    /// after which the limit is removed entirely.
    /// This prevents a freshly started silo from overwhelming itself by claiming all orphaned shards
    /// at once during disaster-recovery scenarios.
    /// Default: 2.
    /// </summary>
    /// <example>
    /// <code>
    /// options.SlowStartInitialBudget = 1;
    /// </code>
    /// </example>
    public int SlowStartInitialBudget { get; set; } = 2;

    /// <summary>
    /// Gets or sets the total number of orphaned shards the silo is allowed to have claimed
    /// by the end of the slow-start ramp-up period. The cumulative budget is linearly
    /// interpolated between <see cref="SlowStartInitialBudget"/> at startup and this value
    /// at <see cref="SlowStartRampUpDuration"/>.
    /// Default: 20.
    /// </summary>
    /// <example>
    /// <code>
    /// options.SlowStartMaxBudget = 50;
    /// </code>
    /// </example>
    public int SlowStartMaxBudget { get; set; } = 20;

    /// <summary>
    /// Gets or sets the duration of the slow-start ramp-up period after silo activation.
    /// While the silo has been running for less than this duration, the number of orphaned shards
    /// it may claim is limited by a linearly increasing budget. Once this period elapses the
    /// silo claims all available orphaned shards without limit.
    /// Set to <see cref="TimeSpan.Zero"/> to disable slow-start entirely.
    /// Default: 5 minutes.
    /// </summary>
    /// <example>
    /// <code>
    /// options.SlowStartRampUpDuration = TimeSpan.FromMinutes(10);
    /// </code>
    /// </example>
    public TimeSpan SlowStartRampUpDuration { get; set; } = TimeSpan.FromMinutes(5);

    private static DateTimeOffset? DefaultShouldRetry(IJobRunContext jobContext, Exception ex)
    {
        // Default retry logic: retry up to 5 times with exponential backoff
        if (jobContext.DequeueCount >= 5)
        {
            return null;
        }
        var delay = TimeSpan.FromSeconds(Math.Pow(2, jobContext.DequeueCount));
        return DateTimeOffset.UtcNow.Add(delay);
    }
}

public sealed class DurableJobsOptionsValidator : IConfigurationValidator
{
    private readonly ILogger<DurableJobsOptionsValidator> _logger;
    private readonly IOptions<DurableJobsOptions> _options;

    public DurableJobsOptionsValidator(ILogger<DurableJobsOptionsValidator> logger, IOptions<DurableJobsOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public void ValidateConfiguration()
    {
        var options = _options.Value;
        if (options.ShardDuration <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException("DurableJobsOptions.ShardDuration must be greater than zero.");
        }
        if (options.ShouldRetry is null)
        {
            throw new OrleansConfigurationException("DurableJobsOptions.ShouldRetry must not be null.");
        }
        if (options.ConcurrencySlowStartEnabled && options.SlowStartInitialConcurrency <= 0)
        {
            throw new OrleansConfigurationException("DurableJobsOptions.SlowStartInitialConcurrency must be greater than zero.");
        }
        if (options.ConcurrencySlowStartEnabled && options.SlowStartInterval <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException("DurableJobsOptions.SlowStartInterval must be greater than zero when slow start is enabled.");
        }
        if (options.ConcurrencySlowStartEnabled && options.SlowStartInitialConcurrency > options.MaxConcurrentJobsPerSilo)
        {
            _logger.LogWarning(
                "DurableJobsOptions.SlowStartInitialConcurrency ({SlowStartInitialConcurrency}) exceeds MaxConcurrentJobsPerSilo ({MaxConcurrentJobsPerSilo}); slow start will not be applied.",
                options.SlowStartInitialConcurrency,
                options.MaxConcurrentJobsPerSilo);
        }
        if (options.MaxAdoptedCount < 0)
        {
            throw new OrleansConfigurationException("DurableJobsOptions.MaxAdoptedCount must be greater than or equal to zero.");
        }
        if (options.SlowStartInitialBudget < 0)
        {
            throw new OrleansConfigurationException("DurableJobsOptions.SlowStartInitialBudget must be non-negative.");
        }
        if (options.SlowStartMaxBudget < options.SlowStartInitialBudget)
        {
            throw new OrleansConfigurationException("DurableJobsOptions.SlowStartMaxBudget must be greater than or equal to SlowStartInitialBudget.");
        }
        if (options.SlowStartRampUpDuration < TimeSpan.Zero)
        {
            throw new OrleansConfigurationException("DurableJobsOptions.SlowStartRampUpDuration must be non-negative.");
        }
        _logger.LogInformation("DurableJobsOptions validated: ShardDuration={ShardDuration}", options.ShardDuration);
    }
}
