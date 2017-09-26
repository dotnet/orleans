﻿using Orleans.LeaseProviders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Runtime.Development
{
    /// <summary>
    /// In memory lease provider for development and test use.
    /// This provider stores lease information in memory an can be lost if grain
    /// becomes inactive or if silo crashes.  This implementation is only intended
    /// for test or local development purposes - NOT FOR PRODUCTION USE.
    /// </summary>
    public class InMemoryLeaseProvider : ILeaseProvider
    {
        private readonly IDevelopmentLeaseProviderGrain leaseProvider;

        public InMemoryLeaseProvider(IGrainFactory grainFactory)
        {
            this.leaseProvider = grainFactory.GetGrain<IDevelopmentLeaseProviderGrain>(0);
        }

        public async Task<AcquireLeaseResult[]> Acquire(string category, LeaseRequest[] leaseRequests)
        {
            try
            {
                return await this.leaseProvider.Acquire(category, leaseRequests);
            } catch(Exception ex)
            {
                return leaseRequests.Select(request => new AcquireLeaseResult(null, ResponseCode.TransientFailure, ex)).ToArray();
            }
        }

        public Task Release(string category, AcquiredLease[] acquiredLeases)
        {
            return this.leaseProvider.Release(category, acquiredLeases);
        }

        public async Task<AcquireLeaseResult[]> Renew(string category, AcquiredLease[] acquiredLeases)
        {
            try
            {
                return await this.leaseProvider.Renew(category, acquiredLeases);
            }
            catch (Exception ex)
            {
                return acquiredLeases.Select(request => new AcquireLeaseResult(null, ResponseCode.TransientFailure, ex)).ToArray();
            }
        }
    }

    public interface IDevelopmentLeaseProviderGrain : ILeaseProvider, IGrainWithIntegerKey
    {
    }

    /// <summary>
    /// Grain that stores lead information in memory.
    /// TODO: Consider making this a stateful grain, as a production viable implementation of lease provider that works with storage
    /// providers.
    /// </summary>
    public class DevelopmentLeaseProviderGrain : Grain, IDevelopmentLeaseProviderGrain
    {
        private readonly Dictionary<Tuple<string, string>, Lease> leases = new Dictionary<Tuple<string, string>, Lease>();

        public Task<AcquireLeaseResult[]> Acquire(string category, LeaseRequest[] leaseRequests)
        {
            return Task.FromResult(leaseRequests.Select(request => Acquire(category, request)).ToArray());
        }

        public Task Release(string category, AcquiredLease[] acquiredLeases)
        {
            foreach(AcquiredLease lease in acquiredLeases)
            {
                Release(category, lease);
            }
            return Task.CompletedTask;
        }

        public Task<AcquireLeaseResult[]> Renew(string category, AcquiredLease[] acquiredLeases)
        {
            return Task.FromResult(acquiredLeases.Select(lease => Renew(category, lease)).ToArray());
        }

        private AcquireLeaseResult Acquire(string category, LeaseRequest leaseRequest)
        {
            DateTime now = DateTime.UtcNow;
            Lease lease = this.leases.GetValueOrAddNew(Tuple.Create(category, leaseRequest.ResourceKey));
            if(lease.ExpiredUtc < now)
            {
                lease.ExpiredUtc = now + leaseRequest.Duration;
                return new AcquireLeaseResult(new AcquiredLease(leaseRequest.ResourceKey, leaseRequest.Duration, lease.Token, now), ResponseCode.OK, null);
            }
            return new AcquireLeaseResult(null, ResponseCode.LeaseNotAvailable, new OrleansException("Lease not available"));
        }

        private void Release(string category, AcquiredLease acquiredLease)
        {
            Tuple<string,string> leaseKey = Tuple.Create(category, acquiredLease.ResourceKey);
            if (this.leases.TryGetValue(leaseKey, out Lease lease) && lease.Token == acquiredLease.Token)
            {
                leases.Remove(leaseKey);
            }
        }

        private AcquireLeaseResult Renew(string category, AcquiredLease acquiredLease)
        {
            DateTime now = DateTime.UtcNow;
            // if lease exists, and we have the right token, and lease has not expired, renew.
            if (!this.leases.TryGetValue(Tuple.Create(category, acquiredLease.ResourceKey), out Lease lease) || lease.Token != acquiredLease.Token)
            {
                return new AcquireLeaseResult(null, ResponseCode.InvalidToken, new OrleansException("Invalid token provided, caller is not the owner."));
            }
            // we don't care if lease has expired or not as long as owner has not changed.
            lease.ExpiredUtc = now + acquiredLease.Duration;
            return new AcquireLeaseResult(new AcquiredLease(acquiredLease.ResourceKey, acquiredLease.Duration, lease.Token, now), ResponseCode.OK, null);
        }

        private class Lease
        {
            public DateTime ExpiredUtc { get; set; }
            public string Token => ExpiredUtc.Ticks.ToString();
        }
    }

}
