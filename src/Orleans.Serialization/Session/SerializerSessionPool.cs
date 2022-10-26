using Microsoft.Extensions.ObjectPool;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;
using System;

namespace Orleans.Serialization.Session
{
    /// <summary>
    /// Pool for <see cref="SerializerSession"/> objects.
    /// </summary>
    public sealed class SerializerSessionPool
    {
        private readonly ObjectPool<SerializerSession> _sessionPool;

        /// <summary>
        /// Initializes a new instance of the <see cref="SerializerSessionPool"/> class.
        /// </summary>
        /// <param name="typeCodec">The type codec.</param>
        /// <param name="wellKnownTypes">The well known type collection.</param>
        /// <param name="codecProvider">The codec provider.</param>
        public SerializerSessionPool(TypeCodec typeCodec, WellKnownTypeCollection wellKnownTypes, CodecProvider codecProvider)
        {
            CodecProvider = codecProvider;
            var sessionPoolPolicy = new SerializerSessionPoolPolicy(typeCodec, wellKnownTypes, codecProvider, ReturnSession);
            _sessionPool = new ConcurrentObjectPool<SerializerSession, SerializerSessionPoolPolicy>(sessionPoolPolicy);
        }

        /// <summary>
        /// Gets the codec provider.
        /// </summary>
        public CodecProvider CodecProvider { get; }

        /// <summary>
        /// Gets a serializer session from the pool.
        /// </summary>
        /// <returns>A serializer session.</returns>
        public SerializerSession GetSession() => _sessionPool.Get();

        /// <summary>
        /// Returns a session to the pool.
        /// </summary>
        /// <param name="session">The session.</param>
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