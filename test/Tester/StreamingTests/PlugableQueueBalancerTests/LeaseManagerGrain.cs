using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Tester.StreamingTests
{
    //one lease manager grain per stream provider, so its key is stream provider name
    public interface ILeaseManagerGrain : IGrainWithStringKey
    {
        Task<QueueId> Acquire();
        Task<bool> Renew(QueueId leaseNumber);
        Task Release(QueueId leaseNumber);
        Task<int> GetLeaseResposibility();
        Task SetQueuesAsLeases(IEnumerable<QueueId> queues);
        //methods used in test asserts
        Task RecordBalancerResponsibility(string balancerId, int ownedQueues);
        Task<Dictionary<string, int>> GetResponsibilityMap();

    }

    public class LeaseManagerGrain : Grain, ILeaseManagerGrain
    {
        //queueId is the lease id here
        private static readonly DateTime UnAssignedLeaseTime = DateTime.MinValue;
        private Dictionary<QueueId, DateTime> queueLeaseToRenewTimeMap;
        private ISiloStatusOracle siloStatusOracle;
        public override Task OnActivateAsync()
        {
            this.siloStatusOracle = base.ServiceProvider.GetRequiredService<ISiloStatusOracle>();
            this.queueLeaseToRenewTimeMap = new Dictionary<QueueId, DateTime>();
            this.responsibilityMap = new Dictionary<string, int>();
            return Task.CompletedTask;
        }
        public Task<int> GetLeaseResposibility()
        {
            var siloCount = this.siloStatusOracle.GetApproximateSiloStatuses(onlyActive: true).Count;
            var resposibity = this.queueLeaseToRenewTimeMap.Count / siloCount;
            return Task.FromResult(resposibity);
        }

        public Task<QueueId> Acquire()
        {
            foreach (var lease in this.queueLeaseToRenewTimeMap)
            {
                //find the first unassigned lease and assign it
                if (lease.Value.Equals(UnAssignedLeaseTime))
                {
                    this.queueLeaseToRenewTimeMap[lease.Key] = DateTime.UtcNow;
                    return Task.FromResult(lease.Key);
                }
            }
            throw new KeyNotFoundException("No more lease to acquire");
        }

        public Task<bool> Renew(QueueId leaseNumber)
        {
            if (this.queueLeaseToRenewTimeMap.ContainsKey(leaseNumber))
            {
                this.queueLeaseToRenewTimeMap[leaseNumber] = DateTime.UtcNow;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task Release(QueueId leaseNumber)
        {
            if (this.queueLeaseToRenewTimeMap.ContainsKey(leaseNumber))
                this.queueLeaseToRenewTimeMap[leaseNumber] = UnAssignedLeaseTime;
            return Task.CompletedTask;
        }

        public Task SetQueuesAsLeases(IEnumerable<QueueId> queueIds)
        {
            //if already set up, return
            if (this.queueLeaseToRenewTimeMap.Count > 0)
                return Task.CompletedTask;
            //set up initial lease map
            foreach (var queueId in queueIds)
            {
                this.queueLeaseToRenewTimeMap.Add(queueId, UnAssignedLeaseTime);
            }
            return Task.CompletedTask;
        }

        //methods used in test asserts
        private Dictionary<string, int> responsibilityMap;
        public Task RecordBalancerResponsibility(string balancerId, int ownedQueues)
        {
            responsibilityMap[balancerId] = ownedQueues;
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, int>> GetResponsibilityMap()
        {
            return Task.FromResult(responsibilityMap);
        }
    }
}
