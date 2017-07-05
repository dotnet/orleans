using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.LeaseProviders
{
    /// <summary>
    /// Aquired lease, which contains the resource key, duration of the lease and the token you can use to renew the lease with
    /// </summary>
    public class AcquiredLease
    {
        public string ResourceKey { get; }
        public TimeSpan Duration { get; }
        public string Token { get; }
    }

    /// <summary>
    /// Lease request where you can specify ResourceKey, which you want to apply a lease on, and duration of your lease. 
    /// </summary>
    public class LeaseRequest
    {
        public string ResourceKey { get; set; }
        public TimeSpan? Duration { get; set; }
    }

    /// <summary>
    /// Lease provider interface 
    /// </summary>
    public interface ILeaseProvider
    {
        Task<AcquiredLease[]> Acquire(string category, LeaseRequest[] requestedLeases);
        Task<AcquiredLease[]> Renew(string category, AcquiredLease[] aquiredLeases);
        Task Release(string category, AcquiredLease[] aquiredLeases);
    }

}
