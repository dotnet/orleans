using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Hosting;

public sealed partial class DurableJobsOptionsValidator
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "DurableJobsOptions.SlowStartInitialConcurrency ({SlowStartInitialConcurrency}) exceeds MaxConcurrentJobsPerSilo ({MaxConcurrentJobsPerSilo}); slow start will not be applied."
    )]
    private static partial void LogWarningSlowStartInitialConcurrencyExceedsMax(ILogger logger, int slowStartInitialConcurrency, int maxConcurrentJobsPerSilo);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "DurableJobsOptions validated: ShardDuration={ShardDuration}"
    )]
    private static partial void LogInformationOptionsValidated(ILogger logger, TimeSpan shardDuration);
}
