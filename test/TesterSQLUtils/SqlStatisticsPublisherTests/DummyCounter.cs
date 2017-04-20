
using Orleans.Runtime;

namespace UnitTests.SqlStatisticsPublisherTests
{
    internal class DummyCounter : ICounter
    {
        public string Name => "DummyCounter";

        public bool IsValueDelta => true;

        public string GetValueString()
        {
            return "GetValueString";
        }

        public string GetDeltaString()
        {
            return "GetDeltaString";
        }

        public void ResetCurrent()
        {
        }

        public string GetDisplayString()
        {
            return "GetDisplayString";
        }

        public CounterStorage Storage => CounterStorage.LogAndTable;

        public void TrackMetric(Logger logger)
        {
        }
    }
}
