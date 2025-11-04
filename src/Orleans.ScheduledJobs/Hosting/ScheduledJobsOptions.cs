using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.ScheduledJobs;

namespace Orleans.Hosting;

/// <summary>
/// Configuration options for the scheduled jobs feature.
/// </summary>
public sealed class ScheduledJobsOptions
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
    /// Gets or sets the function that determines whether a failed job should be retried and when.
    /// The function receives the job context and the exception that caused the failure, and returns
    /// the time when the job should be retried, or <see langword="null"/> if the job should not be retried.
    /// Default: Retry up to 5 times with exponential backoff (2^n seconds).
    /// </summary>
    public Func<IScheduledJobContext, Exception, DateTimeOffset?> ShouldRetry { get; set; } = DefaultShouldRetry;

    private static DateTimeOffset? DefaultShouldRetry(IScheduledJobContext jobContext, Exception ex)
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

public sealed class ScheduledJobsOptionsValidator : IConfigurationValidator
{
    private readonly ILogger<ScheduledJobsOptionsValidator> _logger;
    private readonly IOptions<ScheduledJobsOptions> _options;

    public ScheduledJobsOptionsValidator(ILogger<ScheduledJobsOptionsValidator> logger, IOptions<ScheduledJobsOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public void ValidateConfiguration()
    {
        var options = _options.Value;
        if (options.ShardDuration <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException("ScheduledJobsOptions.ShardDuration must be greater than zero.");
        }
        if (options.ShouldRetry == null)
        {
            throw new OrleansConfigurationException("ScheduledJobsOptions.ShouldRetry must not be null.");
        }
        _logger.LogInformation("ScheduledJobsOptions validated: ShardDuration={ShardDuration}", options.ShardDuration);
    }
}
