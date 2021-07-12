using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.ObjectPool;
using Orleans.Serialization.Invocation;
using System;

namespace Orleans.Serialization.Session
{
    public sealed class SerializerSessionPool
    {
        private readonly ObjectPool<SerializerSession> _sessionPool;

        public SerializerSessionPool(IServiceProvider serviceProvider)
        {
            var sessionPoolPolicy = new SerializerSessionPoolPolicy(serviceProvider, ReturnSession);
            _sessionPool = new ConcurrentObjectPool<SerializerSession, SerializerSessionPoolPolicy>(sessionPoolPolicy);
        }

        public SerializerSession GetSession() => _sessionPool.Get();

        private void ReturnSession(SerializerSession session) => _sessionPool.Return(session);

        private readonly struct SerializerSessionPoolPolicy : IPooledObjectPolicy<SerializerSession>
        {
            private readonly IServiceProvider _serviceProvider;
            private readonly ObjectFactory _factory;
            private readonly Action<SerializerSession> _onSessionDisposed;

            public SerializerSessionPoolPolicy(IServiceProvider serviceProvider, Action<SerializerSession> onSessionDisposed)
            {
                _serviceProvider = serviceProvider;
                _onSessionDisposed = onSessionDisposed;
                _factory = ActivatorUtilities.CreateFactory(typeof(SerializerSession), Array.Empty<Type>());
            }

            public SerializerSession Create()
            {
                var result = (SerializerSession)_factory(_serviceProvider, null);
                result.OnDisposed = _onSessionDisposed;
                return result;
            }

            public bool Return(SerializerSession obj)
            {
                obj.FullReset();
                return true;
            }
        }
    }
}