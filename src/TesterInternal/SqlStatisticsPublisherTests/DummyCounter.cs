using Orleans.Runtime;

namespace UnitTests.SqlStatisticsPublisherTests
{
    internal class DummyCounter : ICounter
    {
        public string Name
        {
            get { return "DummyCounter"; }
        }

        public bool IsValueDelta
        {
            get { return true; }
        }

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

        public CounterStorage Storage
        {
            get { return CounterStorage.LogAndTable; }
        }
    }
}
