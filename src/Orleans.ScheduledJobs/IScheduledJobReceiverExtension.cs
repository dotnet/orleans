using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs;

internal interface IScheduledJobReceiverExtension : IGrainExtension
{
    Task DeliverScheduledJobAsync(IScheduledJob job, CancellationToken cancellationToken);
}

internal sealed class ScheduledJobReceiverExtension : IScheduledJobReceiverExtension
{
    private readonly IGrainContext _grain;
    private readonly ILogger<ScheduledJobReceiverExtension> _logger;

    public ScheduledJobReceiverExtension(IGrainContext grain, ILogger<ScheduledJobReceiverExtension> logger)
    {
        _grain = grain;
        _logger = logger;
    }

    public async Task DeliverScheduledJobAsync(IScheduledJob job, CancellationToken cancellationToken)
    {
        var context = new ScheduledJobContext(job);
        if (_grain.GrainInstance is IScheduledJobHandler handler)
        {
            try
            {
                await handler.ExecuteJobAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing scheduled job {JobId} on grain {GrainId}", job.Id, _grain.GrainId);
                throw;
            }
        }
        else
        {
            _logger.LogError("Grain {GrainId} does not implement IScheduledJobHandler", _grain.GrainId);
            throw new InvalidOperationException($"Grain {_grain.GrainId} does not implement IScheduledJobHandler");
        }
    }

    private class ScheduledJobContext : IScheduledJobContext
    {
        public IScheduledJob Job { get; }

        public ScheduledJobContext(IScheduledJob job)
        {
            Job = job;
        }
    }
}
