
using System;
using System.Threading;
using System.Collections.Concurrent;

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
        private readonly ConcurrentStack<T> pool;
        private readonly Func<T> factoryFunc;
        private long totalObjects;

        /// <summary>
        /// monitor to report statistics for current object pool
        /// </summary>
        private readonly IObjectPoolMonitor monitor;
        private readonly PeriodicAction periodicMonitoring;

        /// <summary>
        /// Simple object pool
        /// </summary>
        /// <param name="factoryFunc">Function used to create new resources of type T</param>
        /// <param name="monitor">monitor to report statistics for object pool</param>
        /// <param name="monitorWriteInterval"></param>
        public ObjectPool(Func<T> factoryFunc, IObjectPoolMonitor monitor = null, TimeSpan? monitorWriteInterval = null)
        {
            if (factoryFunc == null)
            {
                throw new ArgumentNullException("factoryFunc");
            }

            this.factoryFunc = factoryFunc;
            pool = new ConcurrentStack<T>();

            // monitoring
            this.monitor = monitor;
            if (this.monitor != null && monitorWriteInterval.HasValue)
            {
                this.periodicMonitoring = new PeriodicAction(monitorWriteInterval.Value, this.ReportObjectPoolStatistics);
            }

            this.totalObjects = 0;
        }

        /// <summary>
        /// Allocates a pooled resource
        /// </summary>
        /// <returns></returns>
        public virtual T Allocate()
        {
            T resource;
            //if couldn't pop a resource from the pool, create a new resource using factoryFunc from outside of the pool
            if (!pool.TryPop(out resource))
            {
                resource = factoryFunc();
                Interlocked.Increment(ref this.totalObjects);
            }
            this.monitor?.TrackObjectAllocated();
            this.periodicMonitoring?.TryAction(DateTime.UtcNow);
            resource.Pool = this;
            return resource;
        }

        /// <summary>
        /// Returns a resource to the pool
        /// </summary>
        /// <param name="resource"></param>
        public virtual void Free(T resource)
        {
            this.monitor?.TrackObjectReleased();
            this.periodicMonitoring?.TryAction(DateTime.UtcNow);
            pool.Push(resource);
        }

        private void ReportObjectPoolStatistics()
        {
            var availableObjects = this.pool.Count;
            long claimedObjects = this.totalObjects - availableObjects;
            this.monitor.Report(this.totalObjects, availableObjects, claimedObjects);
        }
    }
}
