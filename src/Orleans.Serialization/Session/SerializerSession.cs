using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;
using System;

namespace Orleans.Serialization.Session
{
    public sealed class SerializerSession : IDisposable
    {
        public SerializerSession(TypeCodec typeCodec, WellKnownTypeCollection wellKnownTypes, CodecProvider codecProvider)
        {
            TypeCodec = typeCodec;
            WellKnownTypes = wellKnownTypes;
            CodecProvider = codecProvider;
        }

        public TypeCodec TypeCodec { get; }
        public WellKnownTypeCollection WellKnownTypes { get; }
        public ReferencedTypeCollection ReferencedTypes { get; } = new ReferencedTypeCollection();
        public ReferencedObjectCollection ReferencedObjects { get; } = new ReferencedObjectCollection();
        public CodecProvider CodecProvider { get; }
        internal Action<SerializerSession> OnDisposed { get; set; }

        public void PartialReset() => ReferencedObjects.Reset();

        public void FullReset()
        {
            ReferencedObjects.Reset();
            ReferencedTypes.Reset();
        }

        public void Dispose() => OnDisposed?.Invoke(this);
    }
}