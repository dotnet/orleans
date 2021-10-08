using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans;

namespace Distributed.GrainInterfaces.Streaming
{
    public class ReportingOptions
    {
        public DateTime ReportAt { get; set; }

        public int Duration { get; set; }
    }

    public interface IGrainWithCounter : IGrainWithGuidKey
    {
        Task<int> GetCounterValue(string counterName);
    }

    public interface IImplicitSubscriberGrain : IGrainWithCounter
    {
    }

    public interface ICounterGrain : IGrainWithStringKey
    {
        Task Track(IGrainWithCounter grain);

        Task<TimeSpan> GetRunDuration();

        Task<TimeSpan> WaitTimeForReport();

        Task<int> GetTotalCounterValue(string counterName);
    }
}
