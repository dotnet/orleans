using System;
using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using Orleans;
using StreamPullingAgentBenchmark.EmbeddedSiloLoadTest;

namespace StreamPullingAgentBenchmark.StreamPullingAgentBenchmark
{
    public class StreamPullingAgentBenchmark : BaseEmbeddedSiloLoadTest<BaseOptions>
    {
        private ISharedMemoryCounterAggregatorGrain _subscriberCountGrain;
        private ISharedMemoryCounterAggregatorGrain _eventCountGrain;

        private long _totalSubscriberCount;
        private long _totalEventCount;

        protected override Task InitializeAsync()
        {
            _subscriberCountGrain = SharedMemoryCounterAggregatorGrainFactory.GetGrain(1);
            _eventCountGrain = SharedMemoryCounterAggregatorGrainFactory.GetGrain(0);

            return base.InitializeAsync();
        }

        protected override Task StartPhaseAsync(string phaseName)
        {
            _totalEventCount = 0;

            return TaskDone.Done;
        }

        protected override async Task PollPeriodAsync(string phaseName, int iterationCount, TimeSpan duration)
        {
            long deltaSubscriberCount = await _subscriberCountGrain.Poll();
            _totalSubscriberCount += deltaSubscriberCount;

            long deltaEventCount = await _eventCountGrain.Poll();
            _totalEventCount += deltaEventCount;

            double tps = CalculateTps(deltaEventCount, duration);

            // the following line is parsed by the framework.
            Utilities.LogAlways(string.Format("=*=== Period #{0} ran for {1} sec: Messages Consumed: {2}, Current TPS: {3}, SubscriberCount: {4}", 
                iterationCount, 
                (long)duration.TotalSeconds, 
                deltaEventCount, 
                tps, 
                _totalSubscriberCount));
        }

        protected override Task EndPhaseAsync(string phaseName, TimeSpan duration)
        {
            double avg = _totalEventCount / duration.TotalSeconds;
            Utilities.LogAlways(string.Format("=***= {0} TPS: {1}", phaseName, avg));

            return TaskDone.Done;
        }

        private static double CalculateTps(long count, TimeSpan period)
        {
            if (TimeSpan.Zero == period)
            {
                throw new ArgumentException("Argument is zero", "period");
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException("count", count, "count is less than zero");
            }

            return count / period.TotalSeconds;
        }
    }
}