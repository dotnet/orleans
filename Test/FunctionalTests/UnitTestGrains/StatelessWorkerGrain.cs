using System;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;

using UnitTestGrains;

namespace StatelessWorkerGrain
{
    [StatelessWorker]
    public class StatelessWorkerGrain : Grain, IStatelessWorkerGrain
    {
        private readonly Guid activationGuid = Guid.NewGuid();
        private readonly List<Tuple<DateTime, DateTime>> calls = new List<Tuple<DateTime, DateTime>>();
        private TraceLogger logger;
        private static HashSet<Guid> allActivationIds = new HashSet<Guid>();

        public Task LongCall()
        {
            int count = 0;
            lock (allActivationIds)
            {
                if (!allActivationIds.Contains(activationGuid))
                {
                    allActivationIds.Add(activationGuid);
                }
                count = allActivationIds.Count;
            }
            DateTime start = DateTime.UtcNow;
            //var sw = Stopwatch.StartNew();
            TaskCompletionSource<bool> resolver = new TaskCompletionSource<bool>();
            RegisterTimer(TimerCallback, resolver, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(-1));
            return resolver.Task.ContinueWith(
                (_) =>
                {
                    //sw.Stop();
                    DateTime stop = DateTime.UtcNow;
                    calls.Add(new Tuple<DateTime, DateTime>(start,stop));
                    Trace.WriteLine((stop-start).TotalMilliseconds);
                    if (logger == null)
                    {
                        logger = TraceLogger.GetLogger(activationGuid.ToString());
                    }
                    logger.Info("Start {0}, stop {1}, duration {2}. #act {3}", TraceLogger.PrintDate(start), TraceLogger.PrintDate(stop), (stop - start), count);
                });
        }

        private static Task TimerCallback(object state)
        {
            ((TaskCompletionSource<bool>)state).SetResult(true);
            return TaskDone.Done;
        }


        public Task<Tuple<Guid, List<Tuple<DateTime, DateTime>>>> GetCallStats()
        {
            Thread.Sleep(200);
            if (logger == null)
            {
                logger = TraceLogger.GetLogger(activationGuid.ToString());
            }
            lock (allActivationIds)
            {
                logger.Info("# allActivationIds {0}: {1}", allActivationIds.Count, Utils.EnumerableToString(allActivationIds));
            }
            return Task.FromResult(new Tuple<Guid, List<Tuple<DateTime, DateTime>>>(activationGuid, calls));
        }
    }
}