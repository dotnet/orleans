﻿using Orleans;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Tester.StreamingTests
{
    //Dumb queue balancer only acquire leases once, never renew it, just for testing
    public class LeaseBasedQueueBalancerForTest : IStreamQueueBalancer
    {
        private ILeaseManagerGrain leaseManagerGrain;
        private IGrainFactory grainFactory;
        private string id;
        private List<QueueId> ownedQueues;

        public LeaseBasedQueueBalancerForTest(IGrainFactory grainFactory)
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
