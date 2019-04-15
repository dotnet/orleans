
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    public static class StreamBatchingTestConst
    {
        public const string ProviderName = "StreamBatchingTest";
        public const string BatchingNameSpace = "batching";
        public const string NonBatchingNameSpace = "nonbatching";
    }

    public class ConsumptionReport
    {
        public int Consumed { get; set; }
        public int MaxBatchSize { get; set; }
    }

    public interface IStreamBatchingTestConsumerGrain : IGrainWithGuidKey
    {
        Task<ConsumptionReport> GetConsumptionReport();
    }
}
