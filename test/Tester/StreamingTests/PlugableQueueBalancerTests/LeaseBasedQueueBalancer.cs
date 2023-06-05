using Orleans.Streams;

namespace Tester.StreamingTests
{
    //Dumb queue balancer only acquire leases once, never renew it, just for testing
    public class LeaseBasedQueueBalancerForTest : IStreamQueueBalancer
    {
        private readonly string id;
        private readonly ILeaseManagerGrain leaseManagerGrain;
        private List<QueueId> ownedQueues;

        public LeaseBasedQueueBalancerForTest(string name, IGrainFactory grainFactory)
        {
            leaseManagerGrain = grainFactory.GetGrain<ILeaseManagerGrain>(name);
            id = $"{name}-{Guid.NewGuid()}";
        }

        public async Task Initialize(IStreamQueueMapper queueMapper)
        {
            await leaseManagerGrain.SetQueuesAsLeases(queueMapper.GetAllQueues());
            await GetInitialLease();
        }

        public Task Shutdown()
        {
            return Task.CompletedTask;
        }

        public IEnumerable<QueueId> GetMyQueues()
        {
            return ownedQueues;
        }

        private async Task GetInitialLease()
        {
            var responsibilty = await leaseManagerGrain.GetLeaseResposibility();
            ownedQueues = new List<QueueId>(responsibilty);
            for(var i = 0; i < responsibilty; i++)
            {
                try
                {
                    ownedQueues.Add(await leaseManagerGrain.Acquire());
                }
                catch (KeyNotFoundException)
                { }   
            }
            await leaseManagerGrain.RecordBalancerResponsibility(id.ToString(), ownedQueues.Count);
        }

        public bool SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            //no op operation
            return true;
        }

        public bool UnSubscribeFromQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            //no op operation
            return true;
        }
    }
}
