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
        private string id;
        private List<QueueId> ownedQueues;

        public LeaseBasedQueueBalancer(ILeaseManagerGrain leaseGrain, string id)
        {
            this.leaseManagerGrain = leaseGrain;
            this.id = id;
        }

        public Task Initialize()
        {
            return GetInitialLease();
        }

        public async Task<IEnumerable<QueueId>> GetMyQueues()
        {
            await this.leaseManagerGrain.RecordBalancerResponsibility(id.ToString(), this.ownedQueues.Count);
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
        }

        public Task<bool> SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            //no op operation
            return Task.FromResult(true);
        }

        public Task<bool> UnSubscribeFromQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            //no op operation
            return Task.FromResult(true);
        }
    }


}
