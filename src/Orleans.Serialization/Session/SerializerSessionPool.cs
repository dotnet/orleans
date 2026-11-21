using System;
using Microsoft.Extensions.ObjectPool;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Serialization.Session
{
    /// <summary>
    /// Pool for <see cref="SerializerSession"/> objects.
    /// </summary>
    public sealed class SerializerSessionPool : IDisposable
    {
        private readonly ConcurrentObjectPool<SerializerSession, SerializerSessionPoolPolicy> _sessionPool;

        /// <summary>
        /// Initializes a new instance of the <see cref="SerializerSessionPool"/> class.
        /// </summary>
        /// <param name="typeCodec">The type codec.</param>
        /// <param name="wellKnownTypes">The well known type collection.</param>
        /// <param name="codecProvider">The codec provider.</param>
        public SerializerSessionPool(TypeCodec typeCodec, WellKnownTypeCollection wellKnownTypes, CodecProvider codecProvider)
        {
            CodecProvider = codecProvider;
            var returner = new WeakPoolReturner<SerializerSession>();
            var sessionPoolPolicy = new SerializerSessionPoolPolicy(typeCodec, wellKnownTypes, codecProvider, returner.Return);
            _sessionPool = new ConcurrentObjectPool<SerializerSession, SerializerSessionPoolPolicy>(sessionPoolPolicy);
            returner.SetPool(_sessionPool);
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

        /// <inheritdoc/>
        public void Dispose() => _sessionPool.Dispose();

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
                obj.Reset();
                return true;
            }
        }
    }
}
