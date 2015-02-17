using System;
using System.Threading;
using Orleans.Runtime;
using Orleans.Streams;
using OrleansProviders.PersistentStream.MockQueueAdapter;

namespace LoadTestGrains.MockStreamProviders
{
    internal class MockQueueAdapterMonitor : IMockQueueAdapterMonitor
    {
        private readonly MockStreamProviderSettings _settings;
        private readonly Logger _logger;
        private DateTime _startTime;
        private DateTime _lastReportTime;
        private long _numProducedTotal;
        private long _numProducedSinceLastReport;
        private long _numBatchesAddedToCache;
        private long _numBatchesAddedToCacheSinceLastReport;
        private double _totalBackPressure;
        private int _backPressureChangeCount;
        private int _batchesPerSecond;
        private Timer _loggingTimer;

        public MockQueueAdapterMonitor(MockStreamProviderSettings settings, Logger logger)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            if (logger == null)
            {
                throw new ArgumentNullException("logger");
            }
            _settings = settings;
            _logger = logger;
        }

        public void AdapterCreated()
        {
            _logger.Info("MockQueueAdapter successfully constructed. TotalQueueCount={0}, NumStreamsPerQueue={1}, TargetBatchesSentPerSecond={2}", 
                _settings.TotalQueueCount, _settings.NumStreamsPerQueue, _settings.TargetBatchesSentPerSecond);
            _loggingTimer = new Timer(OnTimer, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        public void BatchDeliveredToConsumer(Guid streamGuid, string streamNamespace)
        {
            Interlocked.Add(ref _numProducedTotal, 1);
            Interlocked.Add(ref _numProducedSinceLastReport, 1);
        }

        public void ReceiverCreated(QueueId queue)
        {
            _logger.Info("Created MockQueueAdapterReceiver on queue {0}", queue);
        }

        public void AddToCache(int count)
        {
            Interlocked.Add(ref _numBatchesAddedToCache, count);
            Interlocked.Add(ref _numBatchesAddedToCacheSinceLastReport, count);
            _logger.Verbose("Items added to cache {0}", count);
        }

        public void NewCursor(Guid streamGuid, string streamNamespace)
        {
            _logger.Verbose("New Cursor for {0}, {1}", streamGuid, streamNamespace);
        }

        public void LowBackPressure(QueueId queue, int batchesPerSecond, double backPressure)
        {
            _totalBackPressure += backPressure;
            _backPressureChangeCount++;
            // TODO: Should track batchesPerSecond by reciever or average, but for now, just sample
            _batchesPerSecond = batchesPerSecond;
            _logger.Verbose("BACKPRESSURE: Low, batchesPerSecond raised to {0}, backpressure: {1}", batchesPerSecond, backPressure);
        }

        public void HighBackPressure(QueueId queue, int batchesPerSecond, double backPressure)
        {
            _totalBackPressure += backPressure;
            _backPressureChangeCount++;
            _batchesPerSecond = batchesPerSecond;
            _logger.Verbose("BACKPRESSURE: High, batchesPerSecond lowered to {0}, backpressure: {1}", batchesPerSecond, backPressure);
        }

        private void OnTimer(object unused)
        {
            LogTPS();
        }

        private void LogTPS()
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan totalDt = now - _startTime;
            long totalTps = (long)(((double)_numProducedTotal) / ((double)totalDt.TotalSeconds));
            long totalCachedTPS = (long)(((double)_numBatchesAddedToCache) / ((double)totalDt.TotalSeconds));

            if (_lastReportTime == default(DateTime))
            {
                // first time, restart without warmup.
                _logger.Info("TOTAL production till now is {0}, WARMUP TPS since start is {1}.", _numProducedTotal, totalTps);
                _logger.Info("TOTAL cached till now is {0}, WARMUP cached items per sec since start is {1}.", _numBatchesAddedToCache, totalCachedTPS);
                _startTime = now;
                _numProducedTotal = 0;
                _numBatchesAddedToCache = 0;
            }
            else
            {
                TimeSpan lastReportDt = now - _lastReportTime;
                long lastReportTps = (long)(((double)_numProducedSinceLastReport) / ((double)lastReportDt.TotalSeconds));
                long lastReportCacheTps = (long)(((double)_numBatchesAddedToCacheSinceLastReport) / ((double)lastReportDt.TotalSeconds));
                _logger.Info("TOTAL production till now is {0}, average TPS after warmup is {1}, TPS in last window is {2}.", _numProducedTotal, totalTps, lastReportTps);
                _logger.Info("TOTAL cached till now is {0}, cached items per sec in last window is {1}.", _numBatchesAddedToCache, lastReportCacheTps);
            }
            DisplayBackPressure();
            _lastReportTime = now;
            _numProducedSinceLastReport = 0;
            _numBatchesAddedToCacheSinceLastReport = 0;
        }

        private void DisplayBackPressure()
        {
            if (_backPressureChangeCount != 0)
            {
                double averageBackPressure = _totalBackPressure/_backPressureChangeCount;
                _logger.Info("TOTAL back pressure: batchesPerSecond {0}, changes {1}, average {2}.", _batchesPerSecond, _backPressureChangeCount, averageBackPressure);
                _totalBackPressure = 0;
                _backPressureChangeCount = 0;
            }
        }
    }
}