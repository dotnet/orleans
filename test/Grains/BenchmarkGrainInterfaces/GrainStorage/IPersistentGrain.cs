using System;
using System.Threading.Tasks;
using Orleans;

namespace BenchmarkGrainInterfaces.GrainStorage
{
    public class Report
    {
        public bool Success { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    public interface IPersistentGrain : IGrainWithGuidKey
    {
        Task Init(int payloadSize);
        Task<Report> TrySet(int index);
    }
}
