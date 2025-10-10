using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.ScheduledJobs;

namespace UnitTests.GrainInterfaces;

public interface IScheduledJobGrain : IGrainWithStringKey
{
    Task<IScheduledJob> ScheduleJobAsync(string jobName, DateTimeOffset scheduledTime);

    Task<bool> HasJobRan(string jobId);
}
