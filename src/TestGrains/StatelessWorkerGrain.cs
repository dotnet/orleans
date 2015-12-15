using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains
{
    [StatelessWorker(1)]
    public class StatelessWorkerGrain : Grain, IStatelessWorkerGrain
    {
        private Guid activationGuid;
        private readonly List<Tuple<DateTime, DateTime>> calls = new List<Tuple<DateTime, DateTime>>();
        private Logger logger;
        private static HashSet<Guid> allActivationIds = new HashSet<Guid>();

        public override Task OnActivateAsync()
        {
            activationGuid = Guid.NewGuid();
            logger = GetLogger(String.Format("{0}", activationGuid));
            logger.Info("Activate.");
            return TaskDone.Done;
        }

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
            TaskCompletionSource<bool> resolver = new TaskCompletionSource<bool>();
            RegisterTimer(TimerCallback, resolver, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(-1));
            return resolver.Task.ContinueWith(
                (_) =>
                {
                    DateTime stop = DateTime.UtcNow;
                    calls.Add(new Tuple<DateTime, DateTime>(start, stop));
                    logger.Info(((stop - start).TotalMilliseconds).ToString());
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
            lock (allActivationIds)
            {
                logger.Info("# allActivationIds {0}: {1}", allActivationIds.Count, Utils.EnumerableToString(allActivationIds));
            }
            return Task.FromResult(new Tuple<Guid, List<Tuple<DateTime, DateTime>>>(activationGuid, calls));
        }
    }
}
