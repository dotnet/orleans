using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.ScheduledJobs;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public class ScheduledJobGrain : Grain, IScheduledJobGrain, IScheduledJobReceiver
{
    private Dictionary<string, bool> jobRunStatus = new();
    private readonly ILocalScheduledJobManager _localScheduledJobManager;
    private readonly ILogger<ScheduledJobGrain> _logger;

    public ScheduledJobGrain(ILocalScheduledJobManager localScheduledJobManager, ILogger<ScheduledJobGrain> logger)
    {
        _localScheduledJobManager = localScheduledJobManager;
        _logger = logger;
    }

    public Task<bool> HasJobRan(string jobId)
    {
        return Task.FromResult(jobRunStatus.TryGetValue(jobId, out var ran) && ran);
    }

    public Task ReceiveScheduledJobAsync(IScheduledJob job)
    {
        _logger.LogInformation($"Job {job.Id} received at {DateTime.UtcNow}");
        jobRunStatus[job.Id] = true;
        return Task.CompletedTask;
    }

    public async Task<IScheduledJob> ScheduleJobAsync(string jobName, DateTimeOffset scheduledTime)
    {
        var job = await _localScheduledJobManager.ScheduleJobAsync(this.GetGrainId(), jobName, scheduledTime);
        jobRunStatus[job.Id] = false;
        return job;
    }
}
