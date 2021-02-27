using System;
using System.Threading.Tasks;

namespace Orleans.LeaseProviders
{
    /// <summary>
    /// Acquired lease
    /// </summary>
    [Immutable]
    [GenerateSerializer]
    public class AcquiredLease 
    {
        /// <summary>
        /// The resource key which the lease is attached to 
        /// </summary>
        [Id(0)]
        public string ResourceKey { get; }

        /// <summary>
        /// Duration of the acquired lease
        /// </summary>
        [Id(1)]
        public TimeSpan Duration { get; }

        /// <summary>
        /// Lease token, which will be null if acquiring or renewing the lease failed
        /// </summary>
        [Id(2)]
        public string Token { get; }

        /// <summary>
        /// Caller side start time for this lease, which is when the lease is acquired or renewed
        /// </summary>
        [Id(3)]
        public DateTime StartTimeUtc { get; }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="resourceKey"></param>
        /// <param name="duration"></param>
        /// <param name="token"></param>
        /// <param name="startTimeUtc"></param>
        public AcquiredLease(string resourceKey, TimeSpan duration, string token, DateTime startTimeUtc)
        {
            this.ResourceKey = resourceKey;
            this.Duration = duration;
            this.Token = token;
            this.StartTimeUtc = startTimeUtc;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="resourceKey"></param>
        public AcquiredLease(string resourceKey)
        {
            this.ResourceKey = resourceKey;
        }
    }

    /// <summary>
    /// AcquireLeaseResult class, which demonstrates result of acquiring or renewing lease operation
    /// </summary>
    [Immutable]
    [GenerateSerializer]
    public class AcquireLeaseResult
    {
        /// <summary>
        /// Acquired lease, which will be null if acquire or renew operation failed.
        /// </summary>
        [Id(0)]
        public AcquiredLease AcquiredLease { get; }

        /// <summary>
        /// Response status
        /// </summary>
        [Id(1)]
        public ResponseCode StatusCode { get; }

        /// <summary>
        /// If acquiring or renewing the lease failed, this is the exception which caused it. This field would be null if operation succeed.
        /// </summary>
        [Id(2)]
        public Exception FailureException { get; }

        public AcquireLeaseResult(AcquiredLease acquiredLease, ResponseCode statusCode, Exception failureException)
        {
            this.AcquiredLease = acquiredLease;
            this.StatusCode = statusCode;
            this.FailureException = failureException;
        }
    }

    [GenerateSerializer]
    public enum ResponseCode
    {
        /// <summary>
        /// Operation succeed
        /// </summary>
        OK,
        /// <summary>
        /// Lease is owned by other entity
        /// </summary>
        LeaseNotAvailable,
        /// <summary>
        /// The token in the AcquiredLease is invalid, which means the lease expired
        /// </summary>
        InvalidToken,
        /// <summary>
        /// TransientFailure, which should be retriable. 
        /// </summary>
        TransientFailure
    }
    
    /// <summary>
    /// Lease request where you can specify ResourceKey and duration of your lease. 
    /// </summary>
    [GenerateSerializer]
    public class LeaseRequest
    {
        /// <summary>
        /// The key of the resource where you want to apply the lease on
        /// </summary>
        [Id(0)]
        public string ResourceKey { get; set; }

        /// <summary>
        /// Duration of the lease
        /// </summary>
        [Id(1)]
        public TimeSpan Duration { get; set; }

        /// <summary>
        /// Constructor
        /// </summary>
        public LeaseRequest()
        { }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="resourceKey"></param>
        /// <param name="duration"></param>
        public LeaseRequest(string resourceKey, TimeSpan duration)
        {
            this.ResourceKey = resourceKey;
            this.Duration = duration;
        }
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
        Task<AcquireLeaseResult[]> Acquire(string category, LeaseRequest[] leaseRequests);
        /// <summary>
        /// Batch renew lease operation
        /// </summary>
        /// <param name="category">resource category</param>
        /// <param name="aquiredLeases"></param>
        /// <returns>Lease renew results array, whose order is the same with acquiredLeases</returns>
        Task<AcquireLeaseResult[]> Renew(string category, AcquiredLease[] aquiredLeases);
        /// <summary>
        /// Batch release lease operation
        /// </summary>
        /// <param name="category">resource category</param>
        /// <param name="aquiredLeases"></param>
        /// <returns></returns>
        Task Release(string category, AcquiredLease[] aquiredLeases);
    }
}
