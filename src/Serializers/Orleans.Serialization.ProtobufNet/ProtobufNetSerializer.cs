using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Orleans.Concurrency;
using ProtoBuf;

namespace Orleans.Serialization.ProtobufNet
{
    /// <summary>
    /// An implementation of IExternalSerializer for usage with Protobuf types, using the protobuf-net library.
    /// </summary>
    public class ProtobufNetSerializer : IExternalSerializer
    {
        private static readonly ConcurrentDictionary<RuntimeTypeHandle, ProtobufTypeCacheItem> Cache = new ConcurrentDictionary<RuntimeTypeHandle, ProtobufTypeCacheItem>();

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
            cacheItem = new ProtobufTypeCacheItem(itemType);
            Cache.AddOrUpdate(itemType.TypeHandle, cacheItem, (type, val) => cacheItem);
            return cacheItem.IsSupported;
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

        private sealed class WriteAdapter : Stream
        {
            private readonly IBinaryTokenStreamWriter writer;
            public WriteAdapter(IBinaryTokenStreamWriter writer) {
                this.writer = writer;
            }

            public override void Write(byte[] buffer, int offset, int count) => this.writer.Write(buffer, offset, count);

            public override int Read(byte[] buffer, int offset, int count) => throw new NotImplementedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
            public override void Flush() => throw new NotImplementedException();
            public override void SetLength(long value) => throw new NotImplementedException();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotImplementedException();
            public override long Position { get => throw new NotImplementedException(); set=> throw new NotImplementedException(); }
        }

        /// <inheritdoc />
        public void Serialize(object item, ISerializationContext context, Type expectedType)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            ProtoBuf.Serializer.Serialize(new WriteAdapter(context.StreamWriter), item);
        }

        private sealed class ReadAdapter : Stream
        {
            private readonly IBinaryTokenStreamReader reader;

            public ReadAdapter(IBinaryTokenStreamReader reader)
            {
                this.reader = reader;
            }


            public override int Read(byte[] buffer, int offset, int count)
            {
                var remaining = (int)(this.reader.Length - this.reader.CurrentPosition);
                var readCount = Math.Min(count, remaining);
                this.reader.ReadByteArray(buffer, offset, readCount);
                return readCount;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
            public override void Flush() => throw new NotImplementedException();
            public override void SetLength(long value) => throw new NotImplementedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotImplementedException();
            public override long Position { get => throw new NotImplementedException(); set=> throw new NotImplementedException(); }
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

            return ProtoBuf.Serializer.Deserialize(expectedType, new ReadAdapter(context.StreamReader));
        }
    }
}
