﻿
using System;
using System.Collections.Generic;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Object pool that roughly ensures only a specified number of the objects are allowed to be allocated. 
    /// When more objects are allocated then is specified, previously allocated objects will be signaled to be purged in order.
    /// When objects are signaled, they should be returned to the pool.  How this is done is an implementation 
    ///   detail of the object.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class FixedSizeObjectPool<T> : ObjectPool<T>
        where T : PooledResource<T>
    {
        private const int MinObjectCount = 3; // 1MB

        // protected for test reasons
        protected readonly Queue<T> usedObjects = new Queue<T>();
        private readonly object locker = new object();
        private readonly int maxObjectCount;

        /// <summary>
        /// Manages a memory pool of poolSize blocks.
        /// Whenever we've allocated more blocks than the poolSize, we signal the oldest allocated block to purge
        ///   itself and return to the pool.
        /// </summary>
        /// <param name="poolSize"></param>
        public FixedSizeObjectPool(int poolSize, Func<IObjectPool<T>,T> factoryFunc)
            : base(factoryFunc, poolSize)
        {
            if (poolSize < MinObjectCount)
            {
                throw new ArgumentOutOfRangeException("poolSize", "Minimum object count is " + MinObjectCount);
            }
            maxObjectCount = poolSize;
        }

        public override T Allocate()
        {
            T obj;
            lock (locker)
            {
                // if we've used all we are allowed, signal object it has been purged and should be returned to the pool
                if (usedObjects.Count >= maxObjectCount)
                {
                    usedObjects.Dequeue().SignalPurge();
                }

                obj = base.Allocate();

                // track used objects
                usedObjects.Enqueue(obj);
            }

            return obj;
        }

        public override void Free(T resource)
        {
            lock (locker)
            {
                base.Free(resource);
            }
        }
    }
}
