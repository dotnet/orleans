using System;
using System.Threading;

namespace Orleans.Runtime
{
    /// <summary>
    /// Utility class to support pooled objects by allowing them to track the pook they came from and return to it when disposed
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public abstract class PooledResource<T> : IDisposable
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
