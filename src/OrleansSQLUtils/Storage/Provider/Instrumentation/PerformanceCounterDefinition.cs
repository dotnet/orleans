using System.Diagnostics;

namespace Orleans.SqlUtils.StorageProvider.Instrumentation
{
    public class PerformanceCounterDefinition
    {
        private readonly string _categoryName;
        private readonly string _counterName;
        private readonly string _counterHelp;
        private readonly PerformanceCounterType _counterType;
        private WritablePerformanceCounter _counter;

        internal PerformanceCounterDefinition(string categoryName, string counterName, string counterHelp, PerformanceCounterType counterType)
        {
            _categoryName = categoryName;
            _counterName = counterName;
            _counterHelp = counterHelp;
            _counterType = counterType;
        }

        public WritablePerformanceCounter CreatePerformanceCounter()
        {
            if (_counter == null)
                _counter = new WritablePerformanceCounter(_categoryName, _counterName);
            return _counter;
        }

        public WritablePerformanceCounter Counter
        {
            get
            {
                if (_counter == null)
                    _counter = new WritablePerformanceCounter(_categoryName, _counterName);
                return _counter;
            }
        }

        internal CounterCreationData GetCreationData()
        {
            return new CounterCreationData(_counterName, _counterHelp, _counterType);
        }
    }
}