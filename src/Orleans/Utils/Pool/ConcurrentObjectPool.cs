using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Orleans.Runtime
{
    public class ConcurrentObjectPool<T> : IObjectPool<T>
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
}
