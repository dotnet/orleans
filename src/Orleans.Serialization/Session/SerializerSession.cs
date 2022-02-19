using Orleans.Serialization.Serializers;
using Orleans.Serialization.TypeSystem;
using System;

namespace Orleans.Serialization.Session
{
    /// <summary>
    /// Contextual information for a serializer operation.
    /// </summary>
    public sealed class SerializerSession : IDisposable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SerializerSession"/> class.
        /// </summary>
        /// <param name="typeCodec">The type codec.</param>
        /// <param name="wellKnownTypes">The well known types.</param>
        /// <param name="codecProvider">The codec provider.</param>
        public SerializerSession(TypeCodec typeCodec, WellKnownTypeCollection wellKnownTypes, CodecProvider codecProvider)
        {
            TypeCodec = typeCodec;
            WellKnownTypes = wellKnownTypes;
            CodecProvider = codecProvider;
        }

        /// <summary>
        /// Gets the type codec.
        /// </summary>
        /// <value>The type codec.</value>
        public TypeCodec TypeCodec { get; }

        /// <summary>
        /// Gets the well known types collection.
        /// </summary>
        /// <value>The well known types collection.</value>
        public WellKnownTypeCollection WellKnownTypes { get; }

        /// <summary>
        /// Gets the referenced type collection.
        /// </summary>
        /// <value>The referenced type collection.</value>
        public ReferencedTypeCollection ReferencedTypes { get; } = new ReferencedTypeCollection();

        /// <summary>
        /// Gets the referenced object collection.
        /// </summary>
        /// <value>The referenced object collection.</value>
        public ReferencedObjectCollection ReferencedObjects { get; } = new ReferencedObjectCollection();

        /// <summary>
        /// Gets the codec provider.
        /// </summary>
        /// <value>The codec provider.</value>
        public CodecProvider CodecProvider { get; }

        internal Action<SerializerSession> OnDisposed { get; set; }

        /// <summary>
        /// Resets the referenced objects collection.
        /// </summary>
        public void PartialReset() => ReferencedObjects.Reset();

        /// <summary>
        /// Performs a full reset.
        /// </summary>
        public void FullReset()
        {
            ReferencedObjects.Reset();
            ReferencedTypes.Reset();
        }

        public void Dispose() => OnDisposed?.Invoke(this);
    }
}