using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Orleans.Runtime
{

    /// <summary>
    /// Simple object pool Interface.
    /// Objects allocated should be returned to the pool when disposed.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal interface IObjectPool<T> where T : IDisposable
    {
        /// <summary>
        /// Allocates a pooled resource
        /// </summary>
        /// <returns></returns>
        T Allocate();

        /// <summary>
        /// Returns a resource to the pool
        /// </summary>
        /// <param name="resource"></param>
        void Free(T resource);
    }

    internal class ConcurrentObjectPool<T> : IObjectPool<T>
        where T : PooledResource<T>, IDisposable
    {
        private readonly ConcurrentBag<T> _objects;
        private readonly Func<T> _objectGenerator;

        public ConcurrentObjectPool(Func<T> objectGenerator)
        {
            if (objectGenerator == null) throw new ArgumentNullException(nameof(objectGenerator));

            _objects = new ConcurrentBag<T>();
            _objectGenerator = objectGenerator;
        }

        public ConcurrentObjectPool(Func<T> objectGenerator, int preAllocationCount) : this(objectGenerator)
        {
            for (int i = 0; i < preAllocationCount; i++)
            {
                Free(objectGenerator());
            }
        }

        public T Allocate()
        {
            T item;
            if (!_objects.TryTake(out item))
            {
                item = _objectGenerator();
            }

            item.Pool = this;
            return item;
        }

        public void Free(T item)
        {
            _objects.Add(item);
        }
    }

    /// <summary>
    /// Utility class to support pooled objects by allowing them to track the pook they came from and return to it when disposed
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal abstract class PooledResource<T> : IDisposable
        where T : PooledResource<T>, IDisposable
    {
        private IObjectPool<T> _pool;

        /// <summary>
        /// The pool to return this resource to upon disposal.
        /// A pool must set this property upon resource allocation.
        /// </summary>
        public IObjectPool<T> Pool { set { _pool = value; } }

        /// <summary>
        /// If this object is to be used in a fixed size object pool, this call should be
        ///   overridden with the purge implementation that returns the object to the pool.
        /// </summary>
        public virtual void SignalPurge()
        {
            Dispose();
        }

        /// <summary>
        /// Returns item to pool
        /// </summary>
        public void Dispose()
        {
            var localPool = Interlocked.Exchange(ref _pool, null);
            if (localPool != null)
            {
                OnResetState();
                localPool.Free((T)this);
            }
        }

        /// <summary>
        /// Notifies the object that it has been purged, so it can reset itself to
        ///   the state of a newly allocated object.
        /// </summary>
        public virtual void OnResetState()
        {
        }
    }
}