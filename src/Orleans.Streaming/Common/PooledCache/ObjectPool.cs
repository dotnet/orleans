
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
        private readonly int maxRetainedObjects;
        private long totalObjects;
        private int retainedObjects;

        /// <summary>
        /// monitor to report statistics for current object pool
        /// </summary>
        private readonly IObjectPoolMonitor? monitor;
        private readonly PeriodicAction? periodicMonitoring;

        /// <summary>
        /// Simple object pool
        /// </summary>
        /// <param name="factoryFunc">Function used to create new resources of type T</param>
        /// <param name="monitor">monitor to report statistics for object pool</param>
        /// <param name="monitorWriteInterval"></param>
        public ObjectPool(Func<T> factoryFunc, IObjectPoolMonitor? monitor = null, TimeSpan? monitorWriteInterval = null)
            : this(factoryFunc, int.MaxValue, monitor, monitorWriteInterval)
        {
        }

        /// <summary>
        /// Simple object pool.
        /// </summary>
        /// <param name="factoryFunc">Function used to create new resources of type <typeparamref name="T"/>.</param>
        /// <param name="maxRetainedObjects">The maximum number of available objects to retain for reuse.</param>
        /// <param name="monitor">Monitor used to report object pool statistics.</param>
        /// <param name="monitorWriteInterval">The interval between object pool statistic reports.</param>
        public ObjectPool(Func<T> factoryFunc, int maxRetainedObjects, IObjectPoolMonitor? monitor = null, TimeSpan? monitorWriteInterval = null)
        {
            if (factoryFunc == null)
            {
                throw new ArgumentNullException(nameof(factoryFunc));
            }

            if (maxRetainedObjects < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxRetainedObjects));
            }

            this.factoryFunc = factoryFunc;
            this.maxRetainedObjects = maxRetainedObjects;
            pool = new ConcurrentStack<T>();

            // monitoring
            this.monitor = monitor;
            if (this.monitor != null && monitorWriteInterval.HasValue)
            {
                this.periodicMonitoring = new PeriodicAction(monitorWriteInterval.Value, this.ReportObjectPoolStatistics);
            }

            this.totalObjects = 0;
            this.retainedObjects = 0;
        }

        /// <summary>
        /// Allocates a pooled resource
        /// </summary>
        /// <returns></returns>
        public virtual T Allocate()
        {
            //if couldn't pop a resource from the pool, create a new resource using factoryFunc from outside of the pool
            if (!pool.TryPop(out var resource))
            {
                resource = factoryFunc();
                Interlocked.Increment(ref this.totalObjects);
            }
            else
            {
                Interlocked.Decrement(ref this.retainedObjects);
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
            if (TryReserveRetainedSlot())
            {
                pool.Push(resource);
            }
            else
            {
                Interlocked.Decrement(ref this.totalObjects);
            }
        }

        private bool TryReserveRetainedSlot()
        {
            while (true)
            {
                var count = Volatile.Read(ref this.retainedObjects);
                if (count >= this.maxRetainedObjects)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref this.retainedObjects, count + 1, count) == count)
                {
                    return true;
                }
            }
        }

        private void ReportObjectPoolStatistics()
        {
            var availableObjects = Volatile.Read(ref this.retainedObjects);
            long claimedObjects = this.totalObjects - availableObjects;
            this.monitor!.Report(this.totalObjects, availableObjects, claimedObjects); // Only invoked via periodicMonitoring, which is only set when monitor is non-null (see constructor).
        }
    }
}
