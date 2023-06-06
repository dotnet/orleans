using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;


namespace UnitTests.Grains
{
    [StatelessWorker(MaxLocalWorkers)]
    public class StatelessWorkerGrain : Grain, IStatelessWorkerGrain
    {
        public const int MaxLocalWorkers = 1;

        private Guid activationGuid;
        private readonly List<Tuple<DateTime, DateTime>> calls = new List<Tuple<DateTime, DateTime>>();
        private readonly ILogger logger;
        private static readonly HashSet<Guid> allActivationIds = new HashSet<Guid>();

        public StatelessWorkerGrain(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger($"{GetType().Name}-{IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            activationGuid = Guid.NewGuid();
            logger.LogInformation("Activate.");
            return Task.CompletedTask;
        }

        public Task LongCall()
        {
            var count = 0;
            lock (allActivationIds)
            {
                if (!allActivationIds.Contains(activationGuid))
                {
                    allActivationIds.Add(activationGuid);
                }
                count = allActivationIds.Count;
            }
            var start = DateTime.UtcNow;
            var resolver = new TaskCompletionSource<bool>();
            RegisterTimer(TimerCallback, resolver, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(-1));
            return resolver.Task.ContinueWith(
                (_) =>
                {
                    var stop = DateTime.UtcNow;
                    calls.Add(new Tuple<DateTime, DateTime>(start, stop));
                    logger.LogInformation("{DurationMilliseconds}", (stop - start).TotalMilliseconds);
                    logger.LogInformation(
                        "Start {StartDate}, stop {StopDate}, duration {Duration}. #act {Count}",
                        LogFormatter.PrintDate(start),
                        LogFormatter.PrintDate(stop),
                        stop - start,
                        count);
                });
        }

        private static Task TimerCallback(object state)
        {
            ((TaskCompletionSource<bool>)state).SetResult(true);
            return Task.CompletedTask;
        }


        public Task<Tuple<Guid, string, List<Tuple<DateTime, DateTime>>>> GetCallStats()
        {
            Thread.Sleep(200);
            var silo = RuntimeIdentity;
            List<Guid> ids;
            lock (allActivationIds)
            {
                ids = allActivationIds.ToList();
            }

            logger.LogInformation(
                "# AllActivationIds {Count} for silo {Silo}: {Ids}",
                ids.Count,
                silo,
                Utils.EnumerableToString(ids));
            return Task.FromResult(Tuple.Create(activationGuid, silo, calls));
        }

        public Task DummyCall() => Task.CompletedTask;
    }
}
