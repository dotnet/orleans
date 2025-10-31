using System;
using System.Threading.Tasks;
using Orleans.ScheduledJobs;

namespace UnitTests.GrainInterfaces;

public interface ISchedulerGrain : IGrainWithStringKey
{
    Task<ScheduledJob> ScheduleJobOnAnotherGrainAsync(string targetGrainKey, string jobName, DateTimeOffset scheduledTime);
}
