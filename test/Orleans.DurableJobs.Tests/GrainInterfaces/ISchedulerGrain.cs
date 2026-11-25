using System;
using System.Threading.Tasks;
using Orleans.Durable.Jobs;

namespace UnitTests.GrainInterfaces;

public interface ISchedulerGrain : IGrainWithStringKey
{
    Task<DurableJob> ScheduleJobOnAnotherGrainAsync(string targetGrainKey, string jobName, DateTimeOffset scheduledTime);
}
