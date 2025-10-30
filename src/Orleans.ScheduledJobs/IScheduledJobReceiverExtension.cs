using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs;

/// <summary>
/// Extension interface for grains that can receive scheduled job invocations.
/// </summary>
internal interface IScheduledJobReceiverExtension : IGrainExtension
{
    /// <summary>
    /// Delivers a scheduled job to the grain for execution.
    /// </summary>
    /// <param name="context">The context containing information about the scheduled job.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task DeliverScheduledJobAsync(IScheduledJobContext context, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed partial class ScheduledJobReceiverExtension : IScheduledJobReceiverExtension
{
    private readonly IGrainContext _grain;
    private readonly ILogger<ScheduledJobReceiverExtension> _logger;

    public ScheduledJobReceiverExtension(IGrainContext grain, ILogger<ScheduledJobReceiverExtension> logger)
    {
        _grain = grain;
        _logger = logger;
    }

    public async Task DeliverScheduledJobAsync(IScheduledJobContext context, CancellationToken cancellationToken)
    {
        if (_grain.GrainInstance is IScheduledJobHandler handler)
        {
            try
            {
                await handler.ExecuteJobAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                LogErrorExecutingScheduledJob(ex, context.Job.Id, _grain.GrainId);
                throw;
            }
        }
        else
        {
            LogGrainDoesNotImplementHandler(_grain.GrainId);
            throw new InvalidOperationException($"Grain {_grain.GrainId} does not implement IScheduledJobHandler");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing scheduled job {JobId} on grain {GrainId}")]
    private partial void LogErrorExecutingScheduledJob(Exception exception, string jobId, GrainId grainId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Grain {GrainId} does not implement IScheduledJobHandler")]
    private partial void LogGrainDoesNotImplementHandler(GrainId grainId);
}
