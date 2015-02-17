using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LoadTestGrainInterfaces;
using Orleans;
using Orleans.Runtime;

namespace LoadTestGrains
{
    public class SharedMemoryCounters
    {
        public enum CounterIds
        {
            EventsConsumed = 0,
            SubscriberCount,
        };

        private readonly static long[] Counters = new long[2];
        private readonly static Stopwatch[] Stopwatches = { Stopwatch.StartNew(), Stopwatch.StartNew() };
        // the following period must be seconds or greater, or the following code won't work.
        private static TimeSpan ReportPeriod = TimeSpan.FromSeconds(10);

        public static void Add(CounterIds counterId, long quantity, Logger logger)
        {
            Interlocked.Add(ref Counters[(int)counterId], quantity);
            if (IsExpired(counterId))
            {
                TryReport(counterId, logger);
            }
        }

        public static long GetValue(CounterIds counterId)
        {
            return Counters[(int)counterId];
        }

        private static long Flush(CounterIds counterId)
        {
            return Interlocked.Exchange(ref Counters[(int)counterId], 0);
        }

        private static Stopwatch GetStopwatch(CounterIds counterId)
        {
            return Stopwatches[(int)counterId];
        }

        private static bool IsExpired(CounterIds counterId)
        {
            long elapsed = GetStopwatch(counterId).ElapsedMilliseconds;
            return elapsed > ReportPeriod.TotalMilliseconds;
        }

        private static void TryReport(CounterIds counterId, Logger logger)
        {
            int index = (int)counterId;
            lock (Stopwatches[index])
            {
                if (!IsExpired(counterId))
                {
                    return;
                }

                try
                {
                    long n = Flush(counterId);

                    if (logger.IsVerbose)
                    {
                        logger.Verbose("elected to report count ({0}) to aggregator grain for {1}; {2} sec passed since last report.", n, counterId, GetStopwatch(counterId).Elapsed.TotalSeconds);
                    }

                    ISharedMemoryCounterAggregatorGrain g = SharedMemoryCounterAggregatorGrainFactory.GetGrain(index);
                    g.Report(n).Ignore();
                }
                finally
                {
                    Stopwatches[index].Restart();
                }
            }
        }
    }

    public class SharedMemoryCounterAggregatorGrain : Grain, ISharedMemoryCounterAggregatorGrain
    {
        private Logger _logger;
        private long _aggregate;
        private long _reportCount;

        public override Task OnActivateAsync()
        {
            _logger = GetLogger(string.Format("EventCountAggregatorGrain.{0}", (SharedMemoryCounters.CounterIds)this.GetPrimaryKeyLong()));
            _aggregate = 0;
            return TaskDone.Done;
        }

        public Task Report(long quantity)
        {
            _aggregate += quantity;
            ++_reportCount;

            if (_logger.IsVerbose)
            {
                _logger.Verbose("got report #{0} ({1}). new total is {2}", _reportCount, quantity, _aggregate);
            }

            return TaskDone.Done;
        }

        public Task<long> Poll()
        {
            long result = _aggregate;
            long reportCount = _reportCount;
            _aggregate = 0;
            _reportCount = 0;

            if (_logger.IsVerbose)
            {
                _logger.Verbose(string.Format("Got poll request; reporting {0} events consumed to caller (aggregate of {1} reports).", result, reportCount));
            }

            return Task.FromResult(result);
        }
    }
}
