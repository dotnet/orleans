using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.ScheduledJobs;

namespace UnitTests.GrainInterfaces;

public interface IRetryTestGrain : IGrainWithStringKey
{
    Task<IScheduledJob> ScheduleJobAsync(string jobName, DateTimeOffset scheduledTime, IReadOnlyDictionary<string, string> metadata = null);

    Task<bool> HasJobSucceeded(string jobId);

    [AlwaysInterleave]
    Task WaitForJobToSucceed(string jobId);

    Task<int> GetJobExecutionAttemptCount(string jobId);

    Task<List<int>> GetJobDequeueCountHistory(string jobId);

    Task<IScheduledJobContext> GetFinalJobContext(string jobId);
}
