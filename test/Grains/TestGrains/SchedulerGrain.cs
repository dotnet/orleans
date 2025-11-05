using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public class SchedulerGrain : Grain, ISchedulerGrain
{
    private readonly ILocalDurableJobManager _localDurableJobManager;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<SchedulerGrain> _logger;

    public SchedulerGrain(
        ILocalDurableJobManager localDurableJobManager,
        IGrainFactory grainFactory,
        ILogger<SchedulerGrain> logger)
    {
        _localDurableJobManager = localDurableJobManager;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async Task<DurableJob> ScheduleJobOnAnotherGrainAsync(string targetGrainKey, string jobName, DateTimeOffset scheduledTime)
    {
        var targetGrain = _grainFactory.GetGrain<IDurableJobGrain>(targetGrainKey);
        var targetGrainId = targetGrain.GetGrainId();

        _logger.LogInformation(
            "Scheduling job {JobName} on grain {TargetGrainKey} from grain {SourceGrain}",
            jobName,
            targetGrainKey,
            this.GetPrimaryKeyString());

        var job = await _localDurableJobManager.ScheduleJobAsync(
            targetGrainId,
            jobName,
            scheduledTime,
            null,
            CancellationToken.None);

        return job;
    }
}
