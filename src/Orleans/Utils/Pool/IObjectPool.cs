using System;

namespace Orleans.Runtime
{
    /// <summary>
    /// Simple object pool Interface.
    /// Objects allocated should be returned to the pool when disposed.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IObjectPool<T> where T : IDisposable
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
}
