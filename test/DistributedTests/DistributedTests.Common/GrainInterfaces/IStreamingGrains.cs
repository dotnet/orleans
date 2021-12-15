using System;
using System.Threading.Tasks;
using Orleans;

namespace DistributedTests.GrainInterfaces
{
    public static class StreamingConstants
    {
        public const string StreamingProvider = "TestStreamingProvider";
        public const string StreamingNamespace = "TestStreamingNamespace";

        public const string DefaultCounterGrain = "default";
    }

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
