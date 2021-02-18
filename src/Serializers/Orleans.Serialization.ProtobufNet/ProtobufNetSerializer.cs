using System;
using System.Collections.Concurrent;
using System.IO;

namespace Orleans.Serialization.ProtobufNet
{
    /// <summary>
    /// An implementation of IExternalSerializer for usage with Protobuf types, using the protobuf-net library.
    /// </summary>
    public class ProtobufNetSerializer : IExternalSerializer
    {
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, ProtobufTypeCacheItem> Cache = new();

        /// <summary>
        /// Determines whether this serializer has the ability to serialize a particular type.
        /// </summary>
        /// <param name="itemType">The type of the item to be serialized</param>
        /// <returns>A value indicating whether the type can be serialized</returns>
        public bool IsSupportedType(Type itemType)
        {
            if (Cache.TryGetValue(itemType.TypeHandle, out var cacheItem))
            {
                return cacheItem.IsSupported;
            }
            return Cache.GetOrAdd(itemType.TypeHandle, new ProtobufTypeCacheItem(itemType)).IsSupported;
        }

        /// <inheritdoc />
        public object DeepCopy(object source, ICopyContext context)
        {
            if (source == null)
            {
                return null;
            }
            var cacheItem = Cache[source.GetType().TypeHandle];
            return cacheItem.IsImmutable ? source : ProtoBuf.Serializer.DeepClone(source);
        }

        /// <inheritdoc />
        public void Serialize(object item, ISerializationContext context, Type expectedType)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (item == null)
            {
                // Special handling for null value. 
                // Since in this ProtobufSerializer we are usually writing the data lengh as 4 bytes
                // we also have to write the Null object as 4 bytes lengh of zero.
                context.StreamWriter.Write(0);
                return;
            }
            
            using (var stream = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(stream, item);
                // The way we write the data is potentially in-efficinet, 
                // since we are first writing to ProtoBuff's internal CodedOutputStream
                // and then take its internal byte[] and write it into out own BinaryTokenStreamWriter.
                // Writing byte[] to BinaryTokenStreamWriter may sometimes copy the byte[] and sometimes just append ass ArraySegment without copy.
                // In the former case it will be a secodnd copy.
                // It would be more effecient to write directly into BinaryTokenStreamWriter
                // but protobuff does not currently support writing directly into a given arbitary stream
                // (it does support System.IO.Steam but BinaryTokenStreamWriter is not compatible with System.IO.Steam).
                // Alternatively, we could force to always append to BinaryTokenStreamWriter, but that could create a lot of small ArraySegments.
                // The plan is to ask the ProtoBuff team to add support for some "InputStream" interface, like Bond does.
                byte[] outBytes = stream.ToArray();
                context.StreamWriter.Write(outBytes.Length);
                context.StreamWriter.Write(outBytes);
            }
        }

        /// <inheritdoc />
        public object Deserialize(Type expectedType, IDeserializationContext context)
        {
            if (expectedType == null)
            {
                throw new ArgumentNullException(nameof(expectedType));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var reader = context.StreamReader;
            int length = reader.ReadInt();
            byte[] data = reader.ReadBytes(length);

            object message = null;
            using (var stream = new MemoryStream(data))
            {
                message = ProtoBuf.Serializer.Deserialize(expectedType, stream);
            }

            return message;
        }
    }
}
