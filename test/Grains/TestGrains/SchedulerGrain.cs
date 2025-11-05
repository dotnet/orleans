using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public class SchedulerGrain : Grain, ISchedulerGrain
{
    private readonly ILocalScheduledJobManager _localScheduledJobManager;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<SchedulerGrain> _logger;

    public SchedulerGrain(
        ILocalScheduledJobManager localScheduledJobManager,
        IGrainFactory grainFactory,
        ILogger<SchedulerGrain> logger)
    {
        _localScheduledJobManager = localScheduledJobManager;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async Task<ScheduledJob> ScheduleJobOnAnotherGrainAsync(string targetGrainKey, string jobName, DateTimeOffset scheduledTime)
    {
        var targetGrain = _grainFactory.GetGrain<IScheduledJobGrain>(targetGrainKey);
        var targetGrainId = targetGrain.GetGrainId();

        _logger.LogInformation(
            "Scheduling job {JobName} on grain {TargetGrainKey} from grain {SourceGrain}",
            jobName,
            targetGrainKey,
            this.GetPrimaryKeyString());

        var job = await _localScheduledJobManager.ScheduleJobAsync(
            targetGrainId,
            jobName,
            scheduledTime,
            null,
            CancellationToken.None);

        return job;
    }
}
