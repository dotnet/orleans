using Orleans.Runtime;

namespace UnitTests.SqlStatisticsPublisherTests
{
    internal class DummyPerformanceMetrics : IClientPerformanceMetrics, ISiloPerformanceMetrics
    {
        public float CpuUsage {get { return 1; } }
        public long AvailablePhysicalMemory { get { return 2; } }
        public long MemoryUsage { get { return 3; } }
        public long TotalPhysicalMemory { get { return 4; } }
        public int SendQueueLength { get { return 5; } }
        public int ReceiveQueueLength { get { return 6; } }
        public long SentMessages { get { return 7; } }
        public long ReceivedMessages { get { return 8; } }
        public long ConnectedGatewayCount { get { return 9; } }
        public long RequestQueueLength { get { return 10; } }
        public int ActivationCount { get { return 11; } }
        public int RecentlyUsedActivationCount { get { return 12; } }
        public long ClientCount { get { return 13; } }
        public bool IsOverloaded { get { return false; } }

        public void LatchIsOverload(bool overloaded)
        {
        }

        public void UnlatchIsOverloaded()
        {
        }

        public void LatchCpuUsage(float value)
        {
        }

        public void UnlatchCpuUsage()
        {
        }
    }
}
