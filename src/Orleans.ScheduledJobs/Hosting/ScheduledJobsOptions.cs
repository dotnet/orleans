using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.ScheduledJobs;

namespace Orleans.Hosting;

public sealed class ScheduledJobsOptions
{
    public TimeSpan ShardDuration { get; set; } = TimeSpan.FromHours(1);

    public Func<IScheduledJobContext, Exception, DateTimeOffset?> ShouldRetry { get; set; } = DefaultShouldRetry;

    private static DateTimeOffset? DefaultShouldRetry(IScheduledJobContext jobContext, Exception ex)
    {
        // Default retry logic: retry up to 5 times with exponential backoff
        if (jobContext.DequeueCount >= 5)
        {
            return null;
        }
        var delay = TimeSpan.FromSeconds(Math.Pow(2, jobContext.DequeueCount));
        return DateTimeOffset.Now.Add(delay);
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
