using Orleans;
using Orleans.Core;
using Orleans.Placement;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Services;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tester.StreamingTests
{
    //Dumb queue balancer only acquire leases once, never renew it, just for testing
    public class LeaseBasedQueueBalancer : IStreamQueueBalancer
    {
        private ILeaseManagerGrain leaseManagerGrain;
        private IGrainFactory grainFactory;
        private string id;
        private List<QueueId> ownedQueues;

        public LeaseBasedQueueBalancer(IGrainFactory grainFactory)
        {
            this.grainFactory = grainFactory;
        }

        public async Task Initialize(string strProviderName,
            IStreamQueueMapper queueMapper,
            TimeSpan siloMaturityPeriod)
        {
            this.leaseManagerGrain = this.grainFactory.GetGrain<ILeaseManagerGrain>(strProviderName);
            await this.leaseManagerGrain.SetQueuesAsLeases(queueMapper.GetAllQueues());
            this.id = $"{strProviderName}-{Guid.NewGuid()}";
            await GetInitialLease();
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
