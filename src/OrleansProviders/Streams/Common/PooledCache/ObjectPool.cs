
using System;
using System.Collections.Generic;
using Orleans.Runtime;
using System.Threading;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Simple object pool that uses a stack to store available objects.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class ObjectPool<T> : IObjectPool<T>
        where T : PooledResource<T>
    {
        private const int DefaultPoolCapacity = 1 << 10; // 1k
        private readonly Stack<T> pool;
        private readonly Func<T> factoryFunc;
        private long totalBlocks;
        /// <summary>
        /// monitor to report statistics for current object pool
        /// </summary>
        protected IObjectPoolMonitor monitor;

        /// <inheritdoc cref="IObjectPool{T}"/>>
        public string Id { get; private set; }

        /// <summary>
        /// Simple object pool
        /// </summary>
        /// <param name="factoryFunc">Function used to create new resources of type T</param>
        /// <param name="blockPoolId">block pool Id, has to be unique</param>
        /// <param name="initialCapacity">Initial number of items to allocate</param>
        /// <param name="monitor">monitor to report statistics for object pool</param>
        /// <param name="monitorWriteInterval"></param>
        public ObjectPool(Func<T> factoryFunc, string blockPoolId, int initialCapacity = DefaultPoolCapacity, IObjectPoolMonitor monitor = null, TimeSpan? monitorWriteInterval = null)
        {
            if (factoryFunc == null)
            {
                throw new ArgumentNullException("factoryFunc");
            }
            if (initialCapacity < 0)
            {
                throw new ArgumentOutOfRangeException("initialCapacity");
            }
            this.factoryFunc = factoryFunc;
            pool = new Stack<T>(initialCapacity);
            this.Id = blockPoolId;
            this.monitor = monitor;

            if (this.monitor != null && monitorWriteInterval.HasValue)
            {
                var timer = new Timer(this.ReportObjectPoolStatistics, null, monitorWriteInterval.Value, monitorWriteInterval.Value);
            }
            this.totalBlocks = 0;
        }

        /// <summary>
        /// Allocates a pooled resource
        /// </summary>
        /// <returns></returns>
        public virtual T Allocate()
        {
            T resource;
            if (pool.Count != 0)
            {
                resource = pool.Pop();
            }
            else
            {
                resource = factoryFunc();
                this.totalBlocks++;
            }
            this.monitor?.TrackObjectAllocatedByCache(1);
            resource.Pool = this;
            return resource;
        }

        /// <summary>
        /// Returns a resource to the pool
        /// </summary>
        /// <param name="resource"></param>
        public virtual void Free(T resource)
        {
            this.monitor?.TrackObjectReleasedFromCache(1);
            pool.Push(resource);
        }

        private void ReportObjectPoolStatistics(object state)
        {
            var freeBlocks = this.pool.Count;
            long claimedBlocks = this.totalBlocks - freeBlocks;
            this.monitor.Report(this.totalBlocks, freeBlocks, claimedBlocks);
        }
    }
}
