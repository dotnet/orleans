using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;

namespace Orleans.Transactions.TestKit.Base.Grains
{
    [Reentrant, StatelessWorker]
    public class DeadlockCoordinator: Grain, IDeadlockCoordinator
    {

        private readonly Random random = new Random();
        private readonly ILogger<DeadlockCoordinator> logger;
        public DeadlockCoordinator(ILogger<DeadlockCoordinator> logger)
        {
            this.logger = logger;
        }


        public async Task RunOrdered(params int[] order)
        {
            for (int i = 0; i < order.Length; i++)
            {
                var delay = TimeSpan.FromMilliseconds(random.NextDouble() * 200);
                logger.LogError($"starting {order[i]} with {delay}");
                await GrainFactory.GetGrain<IDelayedGrain>(order[i])
                    .UpdateState(delay, $"changed-{i}");
                logger.LogError($"finished delayed grain update {order[i]}");
            }
        }

    }
}