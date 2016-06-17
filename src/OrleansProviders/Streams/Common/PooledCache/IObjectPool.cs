
using System;
using System.Threading;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Simple object pool Interface.
    /// Objects allocated should be returned to the pool when disposed.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IObjectPool<T>
        where T : IDisposable
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

    /// <summary>
    /// Utility class to support pooled objects by allowing them to track the pook they came from and return to it when disposed
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract class PooledResource<T> : IDisposable
        where T : class, IDisposable
    {
        private IObjectPool<T> pool;

        /// <summary>
        /// Pooled resource that is from the provided pool
        /// </summary>
        /// <param name="pool"></param>
        protected PooledResource(IObjectPool<T> pool)
        {
            if (pool == null)
            {
                throw new ArgumentNullException("pool");
            }
            this.pool = pool;
        }

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
            IObjectPool<T> localPool = Interlocked.Exchange(ref pool, null);
            if (localPool != null)
            {
                OnResetState();
                localPool.Free(this as T);
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
