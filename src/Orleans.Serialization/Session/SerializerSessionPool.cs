using Microsoft.Extensions.ObjectPool;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;
using System;

namespace Orleans.Serialization.Session
{
    public sealed class SerializerSessionPool
    {
        private readonly ObjectPool<SerializerSession> _sessionPool;

        public SerializerSessionPool(TypeCodec typeCodec, WellKnownTypeCollection wellKnownTypes, CodecProvider codecProvider)
        {
            var sessionPoolPolicy = new SerializerSessionPoolPolicy(typeCodec, wellKnownTypes, codecProvider, ReturnSession);
            _sessionPool = new ConcurrentObjectPool<SerializerSession, SerializerSessionPoolPolicy>(sessionPoolPolicy);
        }

        public SerializerSession GetSession() => _sessionPool.Get();

        private void ReturnSession(SerializerSession session) => _sessionPool.Return(session);

        private readonly struct SerializerSessionPoolPolicy : IPooledObjectPolicy<SerializerSession>
        {
            private readonly TypeCodec _typeCodec;
            private readonly WellKnownTypeCollection _wellKnownTypes;
            private readonly CodecProvider _codecProvider;
            private readonly Action<SerializerSession> _onSessionDisposed;

            public SerializerSessionPoolPolicy(TypeCodec typeCodec, WellKnownTypeCollection wellKnownTypes, CodecProvider codecProvider, Action<SerializerSession> onSessionDisposed)
            {
                _typeCodec = typeCodec;
                _wellKnownTypes = wellKnownTypes;
                _codecProvider = codecProvider;
                _onSessionDisposed = onSessionDisposed;
            }

            public SerializerSession Create()
            {
                return new SerializerSession(_typeCodec, _wellKnownTypes, _codecProvider)
                {
                    OnDisposed = _onSessionDisposed
                };
            }

            public bool Return(SerializerSession obj)
            {
                obj.FullReset();
                return true;
            }
        }
    }
}