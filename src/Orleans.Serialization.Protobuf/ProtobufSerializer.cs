using System;
using System.Collections.Concurrent;
using System.Reflection;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Orleans.Serialization
{
    /// <summary>
    /// An implementation of IExternalSerializer for usage with Protobuf types.
    /// </summary>
    public class ProtobufSerializer : IExternalSerializer
    {
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, MessageParser> Parsers = new ConcurrentDictionary<RuntimeTypeHandle, MessageParser>();

        /// <summary>
        /// Determines whether this serializer has the ability to serialize a particular type.
        /// </summary>
        /// <param name="itemType">The type of the item to be serialized</param>
        /// <returns>A value indicating whether the type can be serialized</returns>
        public bool IsSupportedType(Type itemType)
        {
            if (typeof(IMessage).IsAssignableFrom(itemType))
            {
                if (!Parsers.ContainsKey(itemType.TypeHandle))
                {
                    var prop = itemType.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static);
                    if (prop == null)
                    {
                        return false;
                    }

                    var parser = prop.GetValue(null, null);
                    Parsers.TryAdd(itemType.TypeHandle, parser as MessageParser);
                }
                return true;
            }
            return false;
        }

        /// <inheritdoc />
        public object DeepCopy(object source, ICopyContext context)
        {
            if (source == null)
            {
                return null;
            }

            dynamic dynamicSource = source;
            return dynamicSource.Clone();
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

            IMessage iMessage = item as IMessage;
            if (iMessage == null)
            {
                throw new ArgumentException("The provided item for serialization in not an instance of " + typeof(IMessage), "item");
            }
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
            byte[] outBytes = iMessage.ToByteArray();
            context.StreamWriter.Write(outBytes.Length);
            context.StreamWriter.Write(outBytes);
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

            var typeHandle = expectedType.TypeHandle;
            MessageParser parser = null;
            if (!Parsers.TryGetValue(typeHandle, out parser))
            {
                throw new ArgumentException("No parser found for the expected type " + expectedType, nameof(expectedType));
            }

            var reader = context.StreamReader;
            int length = reader.ReadInt();
            byte[] data = reader.ReadBytes(length);

            object message = parser.ParseFrom(data);

            return message;
        }
    }
}