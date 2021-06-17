using Microsoft.Extensions.ObjectPool;

namespace Orleans.Serialization.Invocation
{
    public static class InvokablePool
    {
        public static T Get<T>() where T : class, IInvokable, new() => TypedPool<T>.Pool.Get();

        public static void Return<T>(T obj) where T : class, IInvokable, new() => TypedPool<T>.Pool.Return(obj);

        private static class TypedPool<T> where T : class, IInvokable, new()
        {
            public static readonly DefaultObjectPool<T> Pool = new DefaultObjectPool<T>(new DefaultPooledObjectPolicy<T>());
        }
    }
}