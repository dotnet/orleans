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
            this.leaseManagerGrain = grainFactory.GetGrain<ILeaseManagerGrain>(name);
            this.id = $"{name}-{Guid.NewGuid()}";
        }

        public async Task Initialize(IStreamQueueMapper queueMapper)
        {
            await this.leaseManagerGrain.SetQueuesAsLeases(queueMapper.GetAllQueues());
            await GetInitialLease();
        }

        public Task Shutdown()
        {
            return Task.CompletedTask;
        }

        public IEnumerable<QueueId> GetMyQueues()
        {
            return this.ownedQueues;
        }

        private async Task GetInitialLease()
        {
            var responsibilty = await this.leaseManagerGrain.GetLeaseResposibility();
            this.ownedQueues = new List<QueueId>(responsibilty);
            for(int i = 0; i < responsibilty; i++)
            {
                try
                {
                    this.ownedQueues.Add(await this.leaseManagerGrain.Acquire());
                }
                catch (KeyNotFoundException)
                { }   
            }
            await this.leaseManagerGrain.RecordBalancerResponsibility(id.ToString(), this.ownedQueues.Count);
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
