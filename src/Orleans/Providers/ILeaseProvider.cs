using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.LeaseProviders
{
    /// <summary>
    /// Acquired lease
    /// </summary>
    public class AcquiredLease
    {
        /// <summary>
        /// The resource key which the lease is attached to 
        /// </summary>
        public string ResourceKey { get; }
        /// <summary>
        /// Duration of the aquired lease
        /// </summary>
        public TimeSpan Duration { get; }
        /// <summary>
        /// Lease token, whcih will be null if acquiring or renewing the lease failed
        /// </summary>
        public string Token { get; }
        /// <summary>
        /// Whether successfully acquire or renew the lease
        /// </summary>
        public bool Success { get; }
        /// <summary>
        /// If acquiring or renewing the lease failed, this is the exception which caused it
        /// </summary>
        public Exception FailureException { get; }
        /// <summary>
        /// Start time for this lease, which is when the lease is acquired or renewed
        /// </summary>
        public DateTime StartTimeUtc { get; }
    }

    /// <summary>
    /// Lease request where you can specify ResourceKey and duration of your lease. 
    /// </summary>
    public class LeaseRequest
    {
        /// <summary>
        /// The key of the resource where you want to apply the lease on
        /// </summary>
        public string ResourceKey { get; set; }
        /// <summary>
        /// Duration of the lease
        /// </summary>
        public TimeSpan? Duration { get; set; }
    }

    /// <summary>
    /// Lease provider interface 
    /// </summary>
    public interface ILeaseProvider
    {
        /// <summary>
        /// Batch acquire leases operation
        /// </summary>
        /// <param name="category">resource category</param>
        /// <param name="leaseRequests"></param>
        /// <returns>Lease acquiring results array, whose order is the same with leaseRequstes</returns>
        Task<AcquiredLease[]> Acquire(string category, LeaseRequest[] leaseRequests);
        /// <summary>
        /// Batch renew lease operation
        /// </summary>
        /// <param name="category">resource category</param>
        /// <param name="aquiredLeases"></param>
        /// <returns>Lease renew results array, whose order is the same with acquiredLeases</returns>
        Task<AcquiredLease[]> Renew(string category, AcquiredLease[] aquiredLeases);
        /// <summary>
        /// Batch release lease operation
        /// </summary>
        /// <param name="category">resource category</param>
        /// <param name="aquiredLeases"></param>
        /// <returns></returns>
        Task Release(string category, AcquiredLease[] aquiredLeases);
    }

}
