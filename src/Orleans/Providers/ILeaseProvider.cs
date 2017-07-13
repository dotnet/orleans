﻿using System;
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
        /// Caller side start time for this lease, which is when the lease is acquired or renewed
        /// </summary>
        public DateTime StartTimeUtc { get; }
    }

    /// <summary>
    /// AcquireLeaseResult class, which demonstrates result of acquiring or renewing lease operation
    /// </summary>
    public class AcquireLeaseResult
    {
        /// <summary>
        /// Acquired lease, which will be null if acquire or renew operation failed.
        /// </summary>
        AcquiredLease AcquiredLease { get; }
        /// <summary>
        /// Response status
        /// </summary>
        public ResponseCode StatusCode { get; }
        /// <summary>
        /// If acquiring or renewing the lease failed, this is the exception which caused it. This field would be null if operation succeed.
        /// </summary>
        public Exception FailureException { get; }
    }

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
    public class LeaseRequest
    {
        /// <summary>
        /// The key of the resource where you want to apply the lease on
        /// </summary>
        public string ResourceKey { get; set; }
        /// <summary>
        /// Duration of the lease
        /// </summary>
        public TimeSpan Duration { get; set; }
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
