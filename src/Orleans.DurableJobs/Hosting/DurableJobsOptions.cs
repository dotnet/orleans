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
    /// Gets or sets the function that determines whether a failed job should be retried and when.
    /// The function receives the job context and the exception that caused the failure, and returns
    /// the time when the job should be retried, or <see langword="null"/> if the job should not be retried.
    /// Default: Retry up to 5 times with exponential backoff (2^n seconds).
    /// </summary>
    public Func<IDurableJobContext, Exception, DateTimeOffset?> ShouldRetry { get; set; } = DefaultShouldRetry;

    private static DateTimeOffset? DefaultShouldRetry(IDurableJobContext jobContext, Exception ex)
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
        if (options.ShouldRetry == null)
        {
            throw new OrleansConfigurationException("DurableJobsOptions.ShouldRetry must not be null.");
        }
        _logger.LogInformation("DurableJobsOptions validated: ShardDuration={ShardDuration}", options.ShardDuration);
    }
}
