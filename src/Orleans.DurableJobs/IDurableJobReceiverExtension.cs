using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

/// <summary>
/// Extension interface for grains that can receive durable job invocations.
/// </summary>
internal interface IDurableJobReceiverExtension : IGrainExtension
{
    /// <summary>
    /// Delivers a durable job to the grain for execution.
    /// </summary>
    /// <param name="context">The context containing information about the durable job.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task DeliverDurableJobAsync(IDurableJobContext context, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed partial class DurableJobReceiverExtension : IDurableJobReceiverExtension
{
    private readonly IGrainContext _grain;
    private readonly ILogger<DurableJobReceiverExtension> _logger;

    public DurableJobReceiverExtension(IGrainContext grain, ILogger<DurableJobReceiverExtension> logger)
    {
        _grain = grain;
        _logger = logger;
    }

    public async Task DeliverDurableJobAsync(IDurableJobContext context, CancellationToken cancellationToken)
    {
        if (_grain.GrainInstance is IDurableJobHandler handler)
        {
            try
            {
                await handler.ExecuteJobAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                LogErrorExecutingDurableJob(ex, context.Job.Id, _grain.GrainId);
                throw;
            }
        }
        else
        {
            LogGrainDoesNotImplementHandler(_grain.GrainId);
            throw new InvalidOperationException($"Grain {_grain.GrainId} does not implement IDurableJobHandler");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing durable job {JobId} on grain {GrainId}")]
    private partial void LogErrorExecutingDurableJob(Exception exception, string jobId, GrainId grainId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Grain {GrainId} does not implement IDurableJobHandler")]
    private partial void LogGrainDoesNotImplementHandler(GrainId grainId);
}
