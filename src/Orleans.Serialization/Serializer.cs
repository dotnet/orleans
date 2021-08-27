using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.IO;

namespace Orleans.Serialization
{
    /// <summary>
    /// Serializes and deserializes values.
    /// </summary>
    public sealed class Serializer 
    {
        private readonly SerializerSessionPool _sessionPool;
        private readonly ICodecProvider _codecProvider;

        public Serializer(SerializerSessionPool sessionPool, ICodecProvider codecProvider)
        {
            _sessionPool = sessionPool;
            _codecProvider = codecProvider;
        }

        /// <summary>
        /// Returns a serializer which is specialized to the provided type parameter.
        /// </summary>
        /// <typeparam name="T">The underlying type for the returned serializer.</typeparam>
        public Serializer<T> GetSerializer<T>() => new(_codecProvider, _sessionPool);

        /// <summary>
        /// Returns <see langword="true"/> if the provided type, <typeparamref name="T"/>, can be serialized, and <see langword="false"/> otherwise.
        /// </summary>
        public bool CanSerialize<T>() => _codecProvider.TryGetCodec(typeof(T)) is { };

        /// <summary>
        /// Returns <see langword="true"/> if the provided type, <paramref name="type"/>, can be serialized, and <see langword="false"/> otherwise.
        /// </summary>
        public bool CanSerialize(Type type) => _codecProvider.TryGetCodec(type) is { };

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into a new array.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="sizeHint">The estimated upper bound for the length of the serialized data.</param>
        /// <returns>A byte array containing the serialized value.</returns>
        public byte[] SerializeToArray<T>(T value, int sizeHint = 0)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(new PooledArrayBufferWriter(sizeHint), session);
            try
            {
                var codec = _codecProvider.GetCodec<T>();
                codec.WriteField(ref writer, 0, typeof(T), value);
                writer.Commit();
                return writer.Output.ToArray();
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize<T>(T value, ref Memory<byte> destination)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec<T>();
            codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize<T>(T value, ref Memory<byte> destination, SerializerSession session)
        {
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec<T>();
            codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="sizeHint">The estimated upper bound for the length of the serialized data.</param>
        /// <remarks>The destination stream will not be flushed by this method.</remarks>
        public void Serialize<T>(T value, Stream destination, int sizeHint = 0)
        {
            if (destination is MemoryStream memoryStream)
            {
                using var session = _sessionPool.GetSession();
                var writer = Writer.Create(memoryStream, session);
                var codec = _codecProvider.GetCodec<T>();
                codec.WriteField(ref writer, 0, typeof(T), value);
                writer.Commit();
            }
            else
            {
                using var session = _sessionPool.GetSession();
                var writer = Writer.CreatePooled(destination, session, sizeHint);
                try
                {
                    var codec = _codecProvider.GetCodec<T>();
                    codec.WriteField(ref writer, 0, typeof(T), value);
                    writer.Commit();
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="sizeHint">The estimated upper bound for the length of the serialized data.</param>
        /// <remarks>The destination stream will not be flushed by this method.</remarks>
        public void Serialize<T>(T value, Stream destination, SerializerSession session, int sizeHint = 0)
        {
            if (destination is MemoryStream memoryStream)
            {
                var buffer = new MemoryStreamBufferWriter(memoryStream);
                var writer = Writer.Create(buffer, session);
                var codec = _codecProvider.GetCodec<T>();
                codec.WriteField(ref writer, 0, typeof(T), value);
                writer.Commit();
            }
            else
            {
                var buffer = new PoolingStreamBufferWriter(destination, sizeHint);
                var writer = Writer.Create(buffer, session);
                try
                {
                    var codec = _codecProvider.GetCodec<T>();
                    codec.WriteField(ref writer, 0, typeof(T), value);
                    writer.Commit();
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <typeparam name="TBufferWriter">The output buffer writer.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        public void Serialize<T, TBufferWriter>(T value, TBufferWriter destination) where TBufferWriter : IBufferWriter<byte>
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec<T>();
            codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();

            // Do not dispose, since the buffer writer is not owned by the method.
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <typeparam name="TBufferWriter">The output buffer writer.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        public void Serialize<T, TBufferWriter>(T value, TBufferWriter destination, SerializerSession session) where TBufferWriter : IBufferWriter<byte>
        {
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec<T>();
            codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();

            // Do not dispose, since the buffer writer is not owned by the method.
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <typeparam name="TBufferWriter">The output buffer writer.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        public void Serialize<T, TBufferWriter>(T value, ref Writer<TBufferWriter> destination) where TBufferWriter : IBufferWriter<byte>
        {
            var codec = _codecProvider.GetCodec<T>();
            codec.WriteField(ref destination, 0, typeof(T), value);
            destination.Commit();
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize<T>(T value, ref Span<byte> destination)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec<T>();
            codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize<T>(T value, ref Span<byte> destination, SerializerSession session)
        {
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec<T>();
            codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <returns>The length of the serialized data.</returns>
        public int Serialize<T>(T value, byte[] destination)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec<T>();
            codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();
            return writer.Position;
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <returns>The length of the serialized data.</returns>
        public int Serialize<T>(T value, ArraySegment<byte> destination)
        {
            var destinationSpan = destination.AsSpan();
            Serialize(value, ref destinationSpan);
            return destinationSpan.Length;
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The length of the serialized data.</returns>
        public int Serialize<T>(T value, ArraySegment<byte> destination, SerializerSession session)
        {
            var destinationSpan = destination.AsSpan();
            Serialize(value, ref destinationSpan, session);
            return destinationSpan.Length;
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="T">The expected type of <paramref name="value"/>.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The length of the serialized data.</returns>
        public int Serialize<T>(T value, byte[] destination, SerializerSession session)
        {
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec<T>();
            codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();
            return writer.Position;
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<T>(Stream source)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(source, session);
            var codec = _codecProvider.GetCodec<T>();
            var field = reader.ReadFieldHeader();
            return codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<T>(Stream source, SerializerSession session)
        {
            var reader = Reader.Create(source, session);
            var codec = _codecProvider.GetCodec<T>();
            var field = reader.ReadFieldHeader();
            return codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<T>(ReadOnlySequence<byte> source)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(source, session);
            var codec = _codecProvider.GetCodec<T>();
            var field = reader.ReadFieldHeader();
            return codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<T>(ReadOnlySequence<byte> source, SerializerSession session)
        {
            var reader = Reader.Create(source, session);
            var codec = _codecProvider.GetCodec<T>();
            var field = reader.ReadFieldHeader();
            return codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<T>(ReadOnlySpan<byte> source)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(source, session);
            var codec = _codecProvider.GetCodec<T>();
            var field = reader.ReadFieldHeader();
            return codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<T>(ReadOnlySpan<byte> source, SerializerSession session)
        {
            var reader = Reader.Create(source, session);
            var codec = _codecProvider.GetCodec<T>();
            var field = reader.ReadFieldHeader();
            return codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<T>(byte[] source) => Deserialize<T>(source.AsSpan());

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<T>(byte[] source, SerializerSession session) => Deserialize<T>(source.AsSpan(), session);

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<T>(ReadOnlyMemory<byte> source) => Deserialize<T>(source.Span);

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<T>(ReadOnlyMemory<byte> source, SerializerSession session) => Deserialize<T>(source.Span, session);

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<T>(ArraySegment<byte> source) => Deserialize<T>(source.AsSpan());

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<T>(ArraySegment<byte> source, SerializerSession session) => Deserialize<T>(source.AsSpan(), session);
        
        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="T">The serialized type.</typeparam>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<T, TInput>(ref Reader<TInput> source)
        {
            var codec = _codecProvider.GetCodec<T>();
            var field = source.ReadFieldHeader();
            return codec.ReadValue(ref source, field);
        }
    }

    /// <summary>
    /// Serializes and deserializes values.
    /// </summary>
    /// <typeparam name="T">The type of value which this instance serializes and deserializes.</typeparam>
    public sealed class Serializer<T>
    {
        private readonly IFieldCodec<T> _codec;
        private readonly SerializerSessionPool _sessionPool;
        private readonly Type _expectedType;

        public Serializer(ICodecProvider codecProvider, SerializerSessionPool sessionPool)
        {
            _expectedType = typeof(T);
            _codec = OrleansGeneratedCodeHelper.UnwrapService(null, codecProvider.GetCodec<T>());
            _sessionPool = sessionPool;
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The output buffer writer.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        public void Serialize<TBufferWriter>(T value, ref Writer<TBufferWriter> destination) where TBufferWriter : IBufferWriter<byte>
        {
            _codec.WriteField(ref destination, 0, _expectedType, value);
            destination.Commit();
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The output buffer writer.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        public void Serialize<TBufferWriter>(T value, TBufferWriter destination) where TBufferWriter : IBufferWriter<byte>
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            _codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();

            // Do not dispose, since the buffer writer is not owned by the method.
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The output buffer writer.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        public void Serialize<TBufferWriter>(T value, TBufferWriter destination, SerializerSession session) where TBufferWriter : IBufferWriter<byte>
        {
            var writer = Writer.Create(destination, session);
            _codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();

            // Do not dispose, since the buffer writer is not owned by the method.
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into a new array.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="sizeHint">The estimated upper bound for the length of the serialized data.</param>
        /// <returns>A byte array containing the serialized value.</returns>
        public byte[] SerializeToArray(T value, int sizeHint = 0)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(new PooledArrayBufferWriter(sizeHint), session);
            try
            {
                _codec.WriteField(ref writer, 0, typeof(T), value);
                writer.Commit();
                return writer.Output.ToArray();
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize(T value, ref Memory<byte> destination)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            _codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize(T value, ref Memory<byte> destination, SerializerSession session)
        {
            var writer = Writer.Create(destination, session);
            _codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize(T value, ref Span<byte> destination)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            _codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize(T value, ref Span<byte> destination, SerializerSession session)
        {
            var writer = Writer.Create(destination, session);
            _codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <returns>The length of the serialized data.</returns>
        public int Serialize(T value, byte[] destination)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            _codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();
            return writer.Position;
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The length of the serialized data.</returns>
        public int Serialize(T value, byte[] destination, SerializerSession session)
        {
            var writer = Writer.Create(destination, session);
            _codec.WriteField(ref writer, 0, typeof(T), value);
            writer.Commit();
            return writer.Position;
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="sizeHint">The estimated upper bound for the length of the serialized data.</param>
        /// <remarks>The destination stream will not be flushed by this method.</remarks>
        public void Serialize(T value, Stream destination, int sizeHint = 0)
        {
            if (destination is MemoryStream memoryStream)
            {
                var buffer = new MemoryStreamBufferWriter(memoryStream);
                using var session = _sessionPool.GetSession();
                var writer = Writer.Create(buffer, session);
                _codec.WriteField(ref writer, 0, typeof(T), value);
                writer.Commit();
            }
            else
            {
                using var session = _sessionPool.GetSession();
                var writer = Writer.Create(new PoolingStreamBufferWriter(destination, sizeHint), session);
                try
                {
                    _codec.WriteField(ref writer, 0, typeof(T), value);
                    writer.Commit();
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="sizeHint">The estimated upper bound for the length of the serialized data.</param>
        /// <remarks>The destination stream will not be flushed by this method.</remarks>
        public void Serialize(T value, Stream destination, SerializerSession session, int sizeHint = 0)
        {
            if (destination is MemoryStream memoryStream)
            {
                var buffer = new MemoryStreamBufferWriter(memoryStream);
                var writer = Writer.Create(buffer, session);
                _codec.WriteField(ref writer, 0, typeof(T), value);
                writer.Commit();
            }
            else
            {
                var writer = Writer.Create(new PoolingStreamBufferWriter(destination, sizeHint), session);
                try
                {
                    _codec.WriteField(ref writer, 0, typeof(T), value);
                    writer.Commit();
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize<TInput>(ref Reader<TInput> source)
        {
            var field = source.ReadFieldHeader();
            return _codec.ReadValue(ref source, field);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize(Stream source)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(source, session);
            var field = reader.ReadFieldHeader();
            return _codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize(Stream source, SerializerSession session)
        {
            var reader = Reader.Create(source, session);
            var field = reader.ReadFieldHeader();
            return _codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize(ReadOnlySequence<byte> source)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(source, session);
            var field = reader.ReadFieldHeader();
            return _codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize(ArraySegment<byte> source) => Deserialize(source.AsSpan());

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize(ReadOnlySequence<byte> source, SerializerSession session)
        {
            var reader = Reader.Create(source, session);
            var field = reader.ReadFieldHeader();
            return _codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize(ReadOnlySpan<byte> source)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(source, session);
            var field = reader.ReadFieldHeader();
            return _codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize(ReadOnlySpan<byte> source, SerializerSession session)
        {
            var reader = Reader.Create(source, session);
            var field = reader.ReadFieldHeader();
            return _codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize(byte[] source) => Deserialize(source.AsSpan());

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize(byte[] source, SerializerSession session) => Deserialize(source.AsSpan(), session);

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize(ReadOnlyMemory<byte> source) => Deserialize(source.Span);

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize(ReadOnlyMemory<byte> source, SerializerSession session) => Deserialize(source.Span, session);

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public T Deserialize(ArraySegment<byte> source, SerializerSession session) => Deserialize(source.AsSpan(), session);
    }

    /// <summary>
    /// Serializes and deserializes value types.
    /// </summary>
    /// <typeparam name="T">The type which this instance operates on.</typeparam>
    public sealed class ValueSerializer<T> where T : struct
    {
        private readonly IValueSerializer<T> _codec;
        private readonly SerializerSessionPool _sessionPool;
        private readonly Type _expectedType;

        public ValueSerializer(IValueSerializerProvider codecProvider, SerializerSessionPool sessionPool)
        {
            _sessionPool = sessionPool;
            _expectedType = typeof(T);
            _codec = OrleansGeneratedCodeHelper.UnwrapService(null, codecProvider.GetValueSerializer<T>());
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The output buffer writer.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        public void Serialize<TBufferWriter>(ref T value, ref Writer<TBufferWriter> destination) where TBufferWriter : IBufferWriter<byte>
        {
            destination.WriteStartObject(0, _expectedType, _expectedType);
            _codec.Serialize(ref destination, ref value);
            destination.WriteEndObject();
            destination.Commit();
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The output buffer writer.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        public void Serialize<TBufferWriter>(ref T value, TBufferWriter destination) where TBufferWriter : IBufferWriter<byte>
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            _codec.Serialize(ref writer, ref value);
            writer.Commit();

            // Do not dispose, since the buffer writer is not owned by the method.
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The output buffer writer.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        public void Serialize<TBufferWriter>(T value, TBufferWriter destination, SerializerSession session) where TBufferWriter : IBufferWriter<byte>
        {
            var writer = Writer.Create(destination, session);
            _codec.Serialize(ref writer, ref value);
            writer.Commit();

            // Do not dispose, since the buffer writer is not owned by the method.
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into a new array.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="sizeHint">The estimated upper bound for the length of the serialized data.</param>
        /// <returns>A byte array containing the serialized value.</returns>
        public byte[] SerializeToArray(ref T value, int sizeHint = 0)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(new PooledArrayBufferWriter(sizeHint), session);
            try
            {
                _codec.Serialize(ref writer, ref value);
                writer.Commit();
                return writer.Output.ToArray();
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize(ref T value, ArraySegment<byte> destination)
        {
            var destinationSpan = destination.AsSpan();
            Serialize(ref value, ref destinationSpan);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize(ref T value, ref Memory<byte> destination)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            _codec.Serialize(ref writer, ref value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize(ref T value, ref Memory<byte> destination, SerializerSession session)
        {
            var writer = Writer.Create(destination, session);
            _codec.Serialize(ref writer, ref value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize(ref T value, ref Span<byte> destination)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            _codec.Serialize(ref writer, ref value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize(ref T value, ref Span<byte> destination, SerializerSession session)
        {
            var writer = Writer.Create(destination, session);
            _codec.Serialize(ref writer, ref value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <returns>The length of the serialized data.</returns>
        public int Serialize(ref T value, byte[] destination)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            _codec.Serialize(ref writer, ref value);
            writer.Commit();
            return writer.Position;
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The length of the serialized data.</returns>
        public int Serialize(ref T value, byte[] destination, SerializerSession session)
        {
            var writer = Writer.Create(destination, session);
            _codec.Serialize(ref writer, ref value);
            writer.Commit();
            return writer.Position;
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="sizeHint">The estimated upper bound for the length of the serialized data.</param>
        /// <remarks>The destination stream will not be flushed by this method.</remarks>
        public void Serialize(ref T value, Stream destination, int sizeHint = 0)
        {
            if (destination is MemoryStream memoryStream)
            {
                var buffer = new MemoryStreamBufferWriter(memoryStream);
                using var session = _sessionPool.GetSession();
                var writer = Writer.Create(buffer, session);
                _codec.Serialize(ref writer, ref value);
                writer.Commit();
            }
            else
            {
                using var session = _sessionPool.GetSession();
                var writer = Writer.Create(new PoolingStreamBufferWriter(destination, sizeHint), session);
                try
                {
                    _codec.Serialize(ref writer, ref value);
                    writer.Commit();
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="sizeHint">The estimated upper bound for the length of the serialized data.</param>
        /// <remarks>The destination stream will not be flushed by this method.</remarks>
        public void Serialize(ref T value, Stream destination, SerializerSession session, int sizeHint = 0)
        {
            if (destination is MemoryStream memoryStream)
            {
                var buffer = new MemoryStreamBufferWriter(memoryStream);
                var writer = Writer.Create(buffer, session);
                _codec.Serialize(ref writer, ref value);
                writer.Commit();
            }
            else
            {
                var writer = Writer.Create(new PoolingStreamBufferWriter(destination, sizeHint), session);
                try
                {
                    _codec.Serialize(ref writer, ref value);
                    writer.Commit();
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <param name="result">The deserialized value.</param>
        /// <returns>The deserialized value.</returns>
        public void Deserialize<TInput>(ref Reader<TInput> source, ref T result)
        {
            Field ignored = default;
            source.ReadFieldHeader(ref ignored);
            _codec.Deserialize(ref source, ref result);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="result">The deserialized value.</param>
        /// <returns>The deserialized value.</returns>
        public void Deserialize(Stream source, ref T result)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(source, session);
            Field ignored = default;
            reader.ReadFieldHeader(ref ignored);
            _codec.Deserialize(ref reader, ref result);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="result">The deserialized value.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public void Deserialize(Stream source, ref T result, SerializerSession session)
        {
            var reader = Reader.Create(source, session);
            Field ignored = default;
            reader.ReadFieldHeader(ref ignored);
            _codec.Deserialize(ref reader, ref result);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="result">The deserialized value.</param>
        /// <returns>The deserialized value.</returns>
        public void Deserialize(ReadOnlySequence<byte> source, ref T result)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(source, session);
            Field ignored = default;
            reader.ReadFieldHeader(ref ignored);
            _codec.Deserialize(ref reader, ref result);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="result">The deserialized value.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public void Deserialize(ReadOnlySequence<byte> source, ref T result, SerializerSession session)
        {
            var reader = Reader.Create(source, session);
            Field ignored = default;
            reader.ReadFieldHeader(ref ignored);
            _codec.Deserialize(ref reader, ref result);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="result">The deserialized value.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public void Deserialize(ArraySegment<byte> source, ref T result, SerializerSession session) => Deserialize(source.AsSpan(), ref result, session);

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="result">The deserialized value.</param>
        /// <returns>The deserialized value.</returns>
        public void Deserialize(ReadOnlySpan<byte> source, ref T result)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(source, session);
            Field ignored = default;
            reader.ReadFieldHeader(ref ignored);
            _codec.Deserialize(ref reader, ref result);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="result">The deserialized value.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public void Deserialize(ReadOnlySpan<byte> source, ref T result, SerializerSession session)
        {
            var reader = Reader.Create(source, session);
            Field ignored = default;
            reader.ReadFieldHeader(ref ignored);
            _codec.Deserialize(ref reader, ref result);
        }

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="result">The deserialized value.</param>
        /// <returns>The deserialized value.</returns>
        public void Deserialize(byte[] source, ref T result) => Deserialize(source.AsSpan(), ref result);

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="result">The deserialized value.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public void Deserialize(byte[] source, ref T result, SerializerSession session) => Deserialize(source.AsSpan(), ref result, session);

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="result">The deserialized value.</param>
        /// <returns>The deserialized value.</returns>
        public void Deserialize(ReadOnlyMemory<byte> source, ref T result) => Deserialize(source.Span, ref result);

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="result">The deserialized value.</param>
        /// <param name="session">The serializer session.</param>
        /// <returns>The deserialized value.</returns>
        public void Deserialize(ReadOnlyMemory<byte> source, ref T result, SerializerSession session) => Deserialize(source.Span, ref result, session);

        /// <summary>
        /// Deserialize a value of type <typeparamref name="T"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="result">The deserialized value.</param>
        /// <returns>The deserialized value.</returns>
        public void Deserialize(ArraySegment<byte> source, ref T result) => Deserialize(source.AsSpan(), ref result);
    }

    /// <summary>
    /// Provides methods for serializing and deserializing values which have types which are not statically known.
    /// </summary>
    public sealed class ObjectSerializer 
    {
        private readonly SerializerSessionPool _sessionPool;
        private readonly ICodecProvider _codecProvider;

        public ObjectSerializer(SerializerSessionPool sessionPool, ICodecProvider codecProvider)
        {
            _sessionPool = sessionPool;
            _codecProvider = codecProvider;
        }

        /// <summary>
        /// Returns <see langword="true"/> if the provided type, <paramref name="type"/>, can be serialized, and <see langword="false"/> otherwise.
        /// </summary>
        public bool CanSerialize(Type type) => _codecProvider.TryGetCodec(type) is { };

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize(object value, ref Memory<byte> destination, Type type)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec(type);
            codec.WriteField(ref writer, 0, type, value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize(object value, ref Memory<byte> destination, SerializerSession session, Type type)
        {
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec(type);
            codec.WriteField(ref writer, 0, type, value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <param name="sizeHint">The estimated upper bound for the length of the serialized data.</param>
        /// <remarks>The destination stream will not be flushed by this method.</remarks>
        public void Serialize(object value, Stream destination, Type type, int sizeHint = 0)
        {
            if (destination is MemoryStream memoryStream)
            {
                using var session = _sessionPool.GetSession();
                var writer = Writer.Create(memoryStream, session);
                var codec = _codecProvider.GetCodec(type);
                codec.WriteField(ref writer, 0, type, value);
                writer.Commit();
            }
            else
            {
                using var session = _sessionPool.GetSession();
                var writer = Writer.CreatePooled(destination, session, sizeHint);
                try
                {
                    var codec = _codecProvider.GetCodec(type);
                    codec.WriteField(ref writer, 0, type, value);
                    writer.Commit();
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <param name="sizeHint">The estimated upper bound for the length of the serialized data.</param>
        /// <remarks>The destination stream will not be flushed by this method.</remarks>
        public void Serialize(object value, Stream destination, SerializerSession session, Type type, int sizeHint = 0)
        {
            if (destination is MemoryStream memoryStream)
            {
                var buffer = new MemoryStreamBufferWriter(memoryStream);
                var writer = Writer.Create(buffer, session);
                var codec = _codecProvider.GetCodec(type);
                codec.WriteField(ref writer, 0, type, value);
                writer.Commit();
            }
            else
            {
                var buffer = new PoolingStreamBufferWriter(destination, sizeHint);
                var writer = Writer.Create(buffer, session);
                try
                {
                    var codec = _codecProvider.GetCodec(type);
                    codec.WriteField(ref writer, 0, type, value);
                    writer.Commit();
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The output buffer writer.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="type">The expected type of the value.</param>
        public void Serialize<TBufferWriter>(object value, TBufferWriter destination, Type type) where TBufferWriter : IBufferWriter<byte>
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec(type);
            codec.WriteField(ref writer, 0, type, value);
            writer.Commit();

            // Do not dispose, since the buffer writer is not owned by the method.
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The output buffer writer.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="type">The expected type of the value.</param>
        public void Serialize<TBufferWriter>(object value, TBufferWriter destination, SerializerSession session, Type type) where TBufferWriter : IBufferWriter<byte>
        {
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec(type);
            codec.WriteField(ref writer, 0, type, value);
            writer.Commit();

            // Do not dispose, since the buffer writer is not owned by the method.
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <typeparam name="TBufferWriter">The output buffer writer.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="type">The expected type of the value.</param>
        public void Serialize<TBufferWriter>(object value, ref Writer<TBufferWriter> destination, Type type) where TBufferWriter : IBufferWriter<byte>
        {
            var codec = _codecProvider.GetCodec(type);
            codec.WriteField(ref destination, 0, type, value);
            destination.Commit();
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize(object value, ref Span<byte> destination, Type type)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec(type);
            codec.WriteField(ref writer, 0, type, value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <remarks>This method slices the <paramref name="destination"/> to the serialized data length.</remarks>
        public void Serialize(object value, ref Span<byte> destination, SerializerSession session, Type type)
        {
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec(type);
            codec.WriteField(ref writer, 0, type, value);
            writer.Commit();
            destination = destination.Slice(0, writer.Position);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The length of the serialized data.</returns>
        public int Serialize(object value, byte[] destination, Type type)
        {
            using var session = _sessionPool.GetSession();
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec(type);
            codec.WriteField(ref writer, 0, type, value);
            writer.Commit();
            return writer.Position;
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The length of the serialized data.</returns>
        public int Serialize(object value, ArraySegment<byte> destination, Type type)
        {
            var destinationSpan = destination.AsSpan();
            Serialize(value, ref destinationSpan, type);
            return destinationSpan.Length;
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The length of the serialized data.</returns>
        public int Serialize(object value, ArraySegment<byte> destination, SerializerSession session, Type type)
        {
            var destinationSpan = destination.AsSpan();
            Serialize(value, ref destinationSpan, session, type);
            return destinationSpan.Length;
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/> into <paramref name="destination"/>.
        /// </summary>
        /// <param name="value">The value to serialize.</param>
        /// <param name="destination">The destination where serialized data will be written.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The length of the serialized data.</returns>
        public int Serialize(object value, byte[] destination, SerializerSession session, Type type)
        {
            var writer = Writer.Create(destination, session);
            var codec = _codecProvider.GetCodec(type);
            codec.WriteField(ref writer, 0, type, value);
            writer.Commit();
            return writer.Position;
        }

        /// <summary>
        /// Deserialize a value of type <paramref name="type"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The deserialized value.</returns>
        public object Deserialize(Stream source, Type type)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(source, session);
            var codec = _codecProvider.GetCodec(type);
            var field = reader.ReadFieldHeader();
            return codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <paramref name="type"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The deserialized value.</returns>
        public object Deserialize(Stream source, SerializerSession session, Type type)
        {
            var reader = Reader.Create(source, session);
            var codec = _codecProvider.GetCodec(type);
            var field = reader.ReadFieldHeader();
            return codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <paramref name="type"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The deserialized value.</returns>
        public object Deserialize(ReadOnlySequence<byte> source, Type type)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(source, session);
            var codec = _codecProvider.GetCodec(type);
            var field = reader.ReadFieldHeader();
            return codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <paramref name="type"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The deserialized value.</returns>
        public object Deserialize(ReadOnlySequence<byte> source, SerializerSession session, Type type)
        {
            var reader = Reader.Create(source, session);
            var codec = _codecProvider.GetCodec(type);
            var field = reader.ReadFieldHeader();
            return codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <paramref name="type"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The deserialized value.</returns>
        public object Deserialize(ReadOnlySpan<byte> source, Type type)
        {
            using var session = _sessionPool.GetSession();
            var reader = Reader.Create(source, session);
            var codec = _codecProvider.GetCodec(type);
            var field = reader.ReadFieldHeader();
            return codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <paramref name="type"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The deserialized value.</returns>
        public object Deserialize(ReadOnlySpan<byte> source, SerializerSession session, Type type)
        {
            var reader = Reader.Create(source, session);
            var codec = _codecProvider.GetCodec(type);
            var field = reader.ReadFieldHeader();
            return codec.ReadValue(ref reader, field);
        }

        /// <summary>
        /// Deserialize a value of type <paramref name="type"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The deserialized value.</returns>
        public object Deserialize(byte[] source, Type type) => Deserialize(source.AsSpan(), type);

        /// <summary>
        /// Deserialize a value of type <paramref name="type"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The deserialized value.</returns>
        public object Deserialize(byte[] source, SerializerSession session, Type type) => Deserialize(source.AsSpan(), session, type);

        /// <summary>
        /// Deserialize a value of type <paramref name="type"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The deserialized value.</returns>
        public object Deserialize(ReadOnlyMemory<byte> source, Type type) => Deserialize(source.Span, type);

        /// <summary>
        /// Deserialize a value of type <paramref name="type"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The deserialized value.</returns>
        public object Deserialize(ReadOnlyMemory<byte> source, SerializerSession session, Type type) => Deserialize(source.Span, session, type);

        /// <summary>
        /// Deserialize a value of type <paramref name="type"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The deserialized value.</returns>
        public object Deserialize(ArraySegment<byte> source, Type type) => Deserialize(source.AsSpan(), type);

        /// <summary>
        /// Deserialize a value of type <paramref name="type"/> from <paramref name="source"/>.
        /// </summary>
        /// <param name="source">The source buffer.</param>
        /// <param name="session">The serializer session.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The deserialized value.</returns>
        public object Deserialize(ArraySegment<byte> source, SerializerSession session, Type type) => Deserialize(source.AsSpan(), session, type);
        
        /// <summary>
        /// Deserialize a value of type <paramref name="type"/> from <paramref name="source"/>.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="source">The source buffer.</param>
        /// <param name="type">The expected type of the value.</param>
        /// <returns>The deserialized value.</returns>
        public object Deserialize<TInput>(ref Reader<TInput> source, Type type)
        {
            var codec = _codecProvider.GetCodec(type);
            var field = source.ReadFieldHeader();
            return codec.ReadValue(ref source, field);
        }
    }

    /// <summary>
    /// Provides functionality for copying object and values.
    /// </summary>
    public sealed class DeepCopier
    {
        private readonly CodecProvider _codecProvider;
        private readonly CopyContextPool _contextPool;

        public DeepCopier(CodecProvider codecProvider, CopyContextPool contextPool)
        {
            _codecProvider = codecProvider;
            _contextPool = contextPool;
        }

        /// <summary>
        /// Returns a copier which is specialized to the provided type parameter.
        /// </summary>
        /// <typeparam name="T">The underlying type for the returned copier.</typeparam>
        public DeepCopier<T> GetCopier<T>() => new(_codecProvider.GetDeepCopier<T>(), _contextPool);

        /// <summary>
        /// Creates a copy of the provided value.
        /// </summary>
        /// <typeparam name="T">The type of the value to copy.</typeparam>
        /// <param name="value">The value to copy.</param>
        /// <returns>A copy of the provided value.</returns>
        public T Copy<T>(T value)
        {
            using var context = _contextPool.GetContext();
            return context.Copy(value);
        }
    }

    /// <summary>
    /// Provides functionality for copying objects and values.
    /// </summary>
    public sealed class DeepCopier<T>
    {
        private readonly IDeepCopier<T> _copier;
        private readonly CopyContextPool _contextPool;

        public DeepCopier(IDeepCopier<T> copier, CopyContextPool contextPool)
        {
            _copier = copier;
            _contextPool = contextPool;
        }

        /// <summary>
        /// Creates a copy of the provided value.
        /// </summary>
        /// <param name="value">The value to copy.</param>
        /// <returns>A copy of the provided value.</returns>
        public T Copy(T value)
        {
            using var context = _contextPool.GetContext();
            return _copier.DeepCopy(value, context);
        }
    }
}