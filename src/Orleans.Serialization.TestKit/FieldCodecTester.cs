using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;
using Orleans.Serialization.Utilities;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text;
using Xunit;
using Orleans.Serialization.Serializers;
using Xunit.Abstractions;
using Orleans.Serialization.GeneratedCodeHelpers;

namespace Orleans.Serialization.TestKit
{
    /// <summary>
    /// Methods for testing field codecs.
    /// </summary>
    [Trait("Category", "BVT")]
    [ExcludeFromCodeCoverage]
    public abstract class FieldCodecTester<TValue, TCodec> : IDisposable where TCodec : class, IFieldCodec<TValue>
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly SerializerSessionPool _sessionPool;

        /// <summary>
        /// Initializes a new instance of the <see cref="FieldCodecTester{TValue, TCodec}"/> class.
        /// </summary>
        protected FieldCodecTester(ITestOutputHelper output)
        {
#if NET6_0_OR_GREATER
            var seed = Random.Shared.Next();
#else
            var seed = new Random().Next();
#endif
            output.WriteLine($"Random seed: {seed}");
            Random = new(seed);
            var services = new ServiceCollection();
            _ = services.AddSerializer(builder => builder.Configure(config => config.FieldCodecs.Add(typeof(TCodec))));

            if (!typeof(TCodec).IsAbstract && !typeof(TCodec).IsInterface)
            {
                _ = services.AddSingleton<TCodec>();
            }

            _ = services.AddSerializer(Configure);

            _serviceProvider = services.BuildServiceProvider();
            _sessionPool = _serviceProvider.GetService<SerializerSessionPool>();
        }

        /// <summary>
        /// Gets the random number generator.
        /// </summary>
        protected Random Random { get; }

        /// <summary>
        /// Gets the service provider.
        /// </summary>
        protected IServiceProvider ServiceProvider => _serviceProvider;

        /// <summary>
        /// Gets the session pool.
        /// </summary>
        protected SerializerSessionPool SessionPool => _sessionPool;

        /// <summary>
        /// Gets the maximum segment sizes for buffer testing.
        /// </summary>
        protected virtual int[] MaxSegmentSizes => [16];

        /// <summary>
        /// Configures the serializer.
        /// </summary>
        protected virtual void Configure(ISerializerBuilder builder)
        {
        }

        /// <summary>
        /// Creates a codec.
        /// </summary>
        protected virtual TCodec CreateCodec() => _serviceProvider.GetRequiredService<TCodec>();

        /// <summary>
        /// Creates a value.
        /// </summary>
        protected abstract TValue CreateValue();

        /// <summary>
        /// Gets test values.
        /// </summary>
        protected abstract TValue[] TestValues { get; }

        /// <summary>
        /// Compares two values for equality.
        /// </summary>
        protected virtual bool Equals(TValue left, TValue right) => EqualityComparer<TValue>.Default.Equals(left, right);

        /// <summary>
        /// Gets a value provider delegate.
        /// </summary>
        protected virtual Action<Action<TValue>> ValueProvider { get; }

        /// <inheritdoc/>
        void IDisposable.Dispose() => (_serviceProvider as IDisposable)?.Dispose();

        protected virtual TValue GetWriteCopy(TValue input) => input;

        /// <summary>
        /// Checks whether the codec correctly advances the reference counter when writing to a stream and reading from a stream.
        /// </summary>
        [Fact]
        public void CorrectlyAdvancesReferenceCounterStream()
        {
            var stream = new MemoryStream();
            using var writerSession = _sessionPool.GetSession();
            using var readerSession = _sessionPool.GetSession();
            var writer = Writer.Create(stream, writerSession);
            var writerCodec = CreateCodec();

            // Write the field. This should involve marking at least one reference in the session.
            Assert.Equal(0, writer.Position);

            foreach (var value in TestValues)
            {
                var beforeReference = writer.Session.ReferencedObjects.CurrentReferenceId;
                writerCodec.WriteField(ref writer, 0, typeof(TValue), value);
                Assert.True(writer.Position > 0);

                writer.Commit();
                var afterReference = writer.Session.ReferencedObjects.CurrentReferenceId;
                Assert.True(beforeReference < afterReference, $"Writing a field should result in at least one reference being marked in the session. Before: {beforeReference}, After: {afterReference}");
                if (value is null)
                {
                    Assert.True(beforeReference + 1 == afterReference, $"Writing a null field should result in exactly one reference being marked in the session. Before: {beforeReference}, After: {afterReference}");
                }

                stream.Flush();

                stream.Position = 0;
                var reader = Reader.Create(stream, readerSession);

                var previousPos = reader.Position;
                Assert.Equal(0, previousPos);
                var readerCodec = CreateCodec();
                var readField = reader.ReadFieldHeader();

                Assert.True(reader.Position > previousPos);
                previousPos = reader.Position;

                beforeReference = reader.Session.ReferencedObjects.CurrentReferenceId;
                var readValue = readerCodec.ReadValue(ref reader, readField);

                Assert.True(reader.Position > previousPos);

                afterReference = reader.Session.ReferencedObjects.CurrentReferenceId;
                Assert.True(beforeReference < afterReference, $"Reading a field should result in at least one reference being marked in the session. Before: {beforeReference}, After: {afterReference}");
                if (readValue is null)
                {
                    Assert.True(beforeReference + 1 == afterReference, $"Reading a null field should result in at exactly one reference being marked in the session. Before: {beforeReference}, After: {afterReference}");
                }
            }
        }

        /// <summary>
        /// Checks whether the codec correctly advances the reference counter when writing to a pipe and reading from a pipe.
        /// </summary>
        [Fact]
        public void CorrectlyAdvancesReferenceCounter()
        {
            var pipe = new Pipe();
            using var writerSession = _sessionPool.GetSession();
            var writer = Writer.Create(pipe.Writer, writerSession);
            var writerCodec = CreateCodec();
            var beforeReference = writer.Session.ReferencedObjects.CurrentReferenceId;

            // Write the field. This should involve marking at least one reference in the session.
            Assert.Equal(0, writer.Position);

            writerCodec.WriteField(ref writer, 0, typeof(TValue), CreateValue());
            Assert.True(writer.Position > 0);

            writer.Commit();
            var afterReference = writer.Session.ReferencedObjects.CurrentReferenceId;
            Assert.True(beforeReference < afterReference, $"Writing a field should result in at least one reference being marked in the session. Before: {beforeReference}, After: {afterReference}");
            _ = pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            pipe.Writer.Complete();

            _ = pipe.Reader.TryRead(out var readResult);
            using var readerSession = _sessionPool.GetSession();
            var reader = Reader.Create(readResult.Buffer, readerSession);

            var previousPos = reader.Position;
            Assert.Equal(0, previousPos);
            var readerCodec = CreateCodec();
            var readField = reader.ReadFieldHeader();

            Assert.True(reader.Position > previousPos);
            previousPos = reader.Position;

            beforeReference = reader.Session.ReferencedObjects.CurrentReferenceId;
            _ = readerCodec.ReadValue(ref reader, readField);

            Assert.True(reader.Position > previousPos);

            pipe.Reader.AdvanceTo(readResult.Buffer.End);
            pipe.Reader.Complete();
            afterReference = reader.Session.ReferencedObjects.CurrentReferenceId;
            Assert.True(beforeReference < afterReference, $"Reading a field should result in at least one reference being marked in the session. Before: {beforeReference}, After: {afterReference}");
        }

        /// <summary>
        /// Checks whether the codec correctly round-trips values when using a pooled stream.
        /// </summary>
        [Fact]
        public void CanRoundTripViaSerializer_StreamPooled()
        {
            var serializer = _serviceProvider.GetRequiredService<Serializer<TValue>>();

            foreach (var original in TestValues)
            {
                Test(original);
            }

            if (ValueProvider is { } valueProvider)
            {
                valueProvider(Test);
            }

            void Test(TValue original)
            {
                var toWrite = GetWriteCopy(original);
                var buffer = new MemoryStream();

                var writer = Writer.CreatePooled(buffer, _sessionPool.GetSession());
                serializer.Serialize(toWrite, ref writer);
                buffer.Flush();
                writer.Dispose();

                buffer.Position = 0;
                var reader = Reader.Create(buffer, _sessionPool.GetSession());
                var deserialized = serializer.Deserialize(ref reader);
                var isEqual = Equals(original, deserialized);
                Assert.True(
                    isEqual,
                    isEqual ? string.Empty : $"Deserialized value \"{deserialized}\" must equal original value \"{original}\"");
            }
        }

        /// <summary>
        /// Checks whether the codec correctly round-trips values when writing to a span.
        /// </summary>
        [Fact]
        public void CanRoundTripViaSerializer_Span()
        {
            var serializer = _serviceProvider.GetRequiredService<Serializer<TValue>>();

            foreach (var original in TestValues)
            {
                Test(original);
            }

            if (ValueProvider is { } valueProvider)
            {
                valueProvider(Test);
            }

            void Test(TValue original)
            {
                var buffer = new byte[8096].AsSpan();
                var toWrite = GetWriteCopy(original);

                var writer = Writer.Create(buffer, _sessionPool.GetSession());
                serializer.Serialize(toWrite, ref writer);

                var reader = Reader.Create(buffer, _sessionPool.GetSession());
                var deserialized = serializer.Deserialize(ref reader);

                var isEqual = Equals(original, deserialized);
                Assert.True(
                    isEqual,
                    isEqual ? string.Empty : $"Deserialized value \"{deserialized}\" must equal original value \"{original}\"");
            }
        }

        /// <summary>
        /// Checks whether the codec correctly round-trips values when writing to an array.
        /// </summary>
        [Fact]
        public void CanRoundTripViaSerializer_Array()
        {
            var serializer = _serviceProvider.GetRequiredService<Serializer<TValue>>();

            foreach (var original in TestValues)
            {
                Test(original);
            }

            if (ValueProvider is { } valueProvider)
            {
                valueProvider(Test);
            }

            void Test(TValue original)
            {
                var buffer = new byte[8096];
                var toWrite = GetWriteCopy(original);

                var writer = Writer.Create(buffer, _sessionPool.GetSession());
                serializer.Serialize(toWrite, ref writer);

                var reader = Reader.Create(buffer, _sessionPool.GetSession());
                var deserialized = serializer.Deserialize(ref reader);

                var isEqual = Equals(original, deserialized);
                Assert.True(
                    isEqual,
                    isEqual ? string.Empty : $"Deserialized value \"{deserialized}\" must equal original value \"{original}\"");
            }
        }

        /// <summary>
        /// Checks whether the codec correctly round-trips values when writing to a memory slice.
        /// </summary>
        [Fact]
        public void CanRoundTripViaSerializer_Memory()
        {
            var serializer = _serviceProvider.GetRequiredService<Serializer<TValue>>();

            foreach (var original in TestValues)
            {
                Test(original);
            }

            if (ValueProvider is { } valueProvider)
            {
                valueProvider(Test);
            }

            void Test(TValue original)
            {
                var buffer = (new byte[8096]).AsMemory();
                var toWrite = GetWriteCopy(original);

                var writer = Writer.Create(buffer, _sessionPool.GetSession());
                serializer.Serialize(toWrite, ref writer);

                var reader = Reader.Create(buffer, _sessionPool.GetSession());
                var deserialized = serializer.Deserialize(ref reader);

                var isEqual = Equals(original, deserialized);
                Assert.True(
                    isEqual,
                    isEqual ? string.Empty : $"Deserialized value \"{deserialized}\" must equal original value \"{original}\"");
            }
        }

        /// <summary>
        /// Checks whether the codec correctly round-trips values when writing to a memory stream.
        /// </summary>
        [Fact]
        public void CanRoundTripViaSerializer_MemoryStream()
        {
            var serializer = _serviceProvider.GetRequiredService<Serializer<TValue>>();

            foreach (var original in TestValues)
            {
                Test(original);
            }

            if (ValueProvider is { } valueProvider)
            {
                valueProvider(Test);
            }

            void Test(TValue original)
            {
                var toWrite = GetWriteCopy(original);
                var buffer = new MemoryStream();
                using var writerSession = _sessionPool.GetSession();
                var writer = Writer.Create(buffer, writerSession);
                serializer.Serialize(toWrite, ref writer);
                writer.Commit();
                buffer.Flush();
                buffer.SetLength(buffer.Position);

                buffer.Position = 0;
                using var readerSession = _sessionPool.GetSession();
                var reader = Reader.Create(buffer, readerSession);
                var deserialized = serializer.Deserialize(ref reader);

                var isEqual = Equals(original, deserialized);
                Assert.True(
                    isEqual,
                    isEqual ? string.Empty : $"Deserialized value \"{deserialized}\" must equal original value \"{original}\"");
                Assert.Equal(writer.Position, reader.Position);
                Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
            }
        }

        /// <summary>
        /// Checks whether the codec correctly round-trips values when reading byte-by-byte, simulating fragmented reads.
        /// </summary>
        [Fact]
        public void CanRoundTripViaSerializer_ReadByteByByte()
        {
            var serializer = _serviceProvider.GetRequiredService<Serializer<TValue>>();

            foreach (var original in TestValues)
            {
                Test(original);
            }

            if (ValueProvider is { } valueProvider)
            {
                valueProvider(Test);
            }

            void Test(TValue original)
            {
                var toWrite = GetWriteCopy(original);
                var buffer = new TestMultiSegmentBufferWriter(maxAllocationSize: 1024);
                using var writerSession = _sessionPool.GetSession();
                var writer = Writer.Create(buffer, writerSession);
                serializer.Serialize(toWrite, ref writer);
                writer.Commit();
                using var readerSession = _sessionPool.GetSession();
                var reader = Reader.Create(buffer.GetReadOnlySequence(maxSegmentSize: 1), readerSession);
                var deserialized = serializer.Deserialize(ref reader);

                var isEqual = Equals(original, deserialized);
                Assert.True(
                    isEqual,
                    isEqual ? string.Empty : $"Deserialized value \"{deserialized}\" must equal original value \"{original}\"");
                Assert.Equal(writer.Position, reader.Position);
                Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
            }
        }

        /// <summary>
        /// Checks whether the codec produces a valid bit stream.
        /// </summary>
        [Fact]
        public void ProducesValidBitStream()
        {
            var serializer = _serviceProvider.GetRequiredService<Serializer<TValue>>();
            foreach (var value in TestValues)
            {
                Test(value);
            }

            if (ValueProvider is { } valueProvider)
            {
                valueProvider(Test);
            }

            void Test(TValue value)
            {
                var array = serializer.SerializeToArray(value);
                var session = _sessionPool.GetSession();
                var reader = Reader.Create(array, session);
                var formatted = new StringBuilder();
                try
                {
                    BitStreamFormatter.Format(ref reader, formatted);
                    Assert.True(formatted.ToString() is string { Length: > 0 });
                }
                catch (Exception exception)
                {
                    Assert.True(false, $"Formatting failed with exception: {exception} and partial result: \"{formatted}\"");
                }
            }
        }

        /// <summary>
        /// Checks whether various buffer writers produce bit-wise identical results.
        /// </summary>
        [Fact]
        public void WritersProduceSameResults()
        {
            var serializer = _serviceProvider.GetRequiredService<Serializer<TValue>>();

            foreach (var original in TestValues)
            {
                Test(original);
            }

            if (ValueProvider is { } valueProvider)
            {
                valueProvider(Test);
            }

            void Test(TValue original)
            {
                byte[] expected;

                {
                    var buffer = new TestMultiSegmentBufferWriter(1024);
                    var writer = Writer.Create(buffer, _sessionPool.GetSession());
                    serializer.Serialize(GetWriteCopy(original), ref writer);
                    expected = buffer.GetReadOnlySequence(0).ToArray();
                }

                {
                    var buffer = new MemoryStream();
                    var writer = Writer.Create(buffer, _sessionPool.GetSession());
                    serializer.Serialize(GetWriteCopy(original), ref writer);
                    buffer.Flush();
                    buffer.SetLength(buffer.Position);
                    buffer.Position = 0;
                    var result = buffer.ToArray();
                    Assert.Equal(expected, result);
                }

                {
                    var writer = Writer.CreatePooled(_sessionPool.GetSession());
                    serializer.Serialize(GetWriteCopy(original), ref writer);
                    var result = writer.Output.ToArray();
                    Assert.Equal(expected, result);
                }

                var bytes = new byte[10240];

                {
                    var buffer = bytes.AsMemory();
                    var writer = Writer.Create(buffer, _sessionPool.GetSession());
                    serializer.Serialize(GetWriteCopy(original), ref writer);
                    var result = buffer[..writer.Output.BytesWritten].ToArray();
                    Assert.Equal(expected, result);
                }

                bytes.AsSpan().Clear();

                {
                    var buffer = bytes.AsSpan();
                    var writer = Writer.Create(buffer, _sessionPool.GetSession());
                    serializer.Serialize(GetWriteCopy(original), ref writer);
                    var result = buffer[..writer.Output.BytesWritten].ToArray();
                    Assert.Equal(expected, result);
                }

                bytes.AsSpan().Clear();

                {
                    var buffer = bytes;
                    var writer = Writer.Create(buffer, _sessionPool.GetSession());
                    serializer.Serialize(GetWriteCopy(original), ref writer);
                    var result = buffer.AsSpan(0, writer.Output.BytesWritten).ToArray();
                    Assert.Equal(expected, result);
                }

                bytes.AsSpan().Clear();

                {
                    var buffer = new MemoryStream(bytes);
                    var writer = Writer.CreatePooled(buffer, _sessionPool.GetSession());
                    serializer.Serialize(GetWriteCopy(original), ref writer);
                    buffer.Flush();
                    buffer.SetLength(buffer.Position);
                    buffer.Position = 0;
                    var result = buffer.ToArray();
                    writer.Dispose();
                    Assert.Equal(expected, result);
                }

                bytes.AsSpan().Clear();

                {
                    var buffer = new MemoryStream(bytes);
                    var writer = Writer.Create((Stream)buffer, _sessionPool.GetSession());
                    serializer.Serialize(GetWriteCopy(original), ref writer);
                    buffer.Flush();
                    buffer.SetLength(buffer.Position);
                    buffer.Position = 0;
                    var result = buffer.ToArray();
                    Assert.Equal(expected, result);
                }
            }
        }

        /// <summary>
        /// Checks whether a strongly typed collection of values can be round-tripped.
        /// </summary>
        [Fact]
        public void CanRoundTripCollectionViaSerializer()
        {
            var serializer = _serviceProvider.GetRequiredService<Serializer<List<TValue>>>();

            var original = new List<TValue>();
            var originalCopy = new List<TValue>();
            original.AddRange(TestValues);
            foreach (var value in original)
            {
                originalCopy.Add(GetWriteCopy(value));
            }

            for (var i = 0; i < 5; i++)
            {
                var o = CreateValue();
                var c = GetWriteCopy(o);
                original.Add(o);
                originalCopy.Add(c);
            }

            using var writerSession = _sessionPool.GetSession();
            var writer = Writer.CreatePooled(writerSession);
            serializer.Serialize(originalCopy, ref writer);
            using var readerSession = _sessionPool.GetSession();
            var reader = Reader.Create(writer.Output, readerSession);
            var deserialized = serializer.Deserialize(ref reader);

            Assert.Equal(original.Count, deserialized.Count);
            for (var i = 0; i < original.Count; ++i)
            {
                var isEqual = Equals(original[i], deserialized[i]);
                Assert.True(
                    isEqual,
                    isEqual ? string.Empty : $"Deserialized value at index {i}, \"{deserialized[i]}\", must equal original value, \"{original[i]}\"");
            }

            Assert.Equal(writer.Position, reader.Position);
            Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
        }

        /// <summary>
        /// Checks whether a strongly typed collection of values can be round-tripped.
        /// </summary>
        [Fact]
        public void CanRoundTripWeaklyTypedCollectionViaSerializer()
        {
            var serializer = _serviceProvider.GetRequiredService<Serializer<List<object>>>();

            var original = new List<object>();
            var originalCopy = new List<object>();
            foreach (var value in TestValues)
            {
                var o = value;
                var c = GetWriteCopy(o);
                original.Add(o);
                originalCopy.Add(c);
            }

            for (var i = 0; i < 5; i++)
            {
                var o = CreateValue();
                var c = GetWriteCopy(o);
                original.Add(o);
                originalCopy.Add(c);
            }

            using var writerSession = _sessionPool.GetSession();
            var writer = Writer.CreatePooled(writerSession);
            serializer.Serialize(originalCopy, ref writer);
            using var readerSession = _sessionPool.GetSession();
            var reader = Reader.Create(writer.Output, readerSession);
            var deserialized = serializer.Deserialize(ref reader);

            Assert.Equal(original.Count, deserialized.Count);
            for (var i = 0; i < original.Count; ++i)
            {
                var left = (TValue)original[i];
                var right = (TValue)deserialized[i];
                var isEqual = Equals(left, right);
                Assert.True(
                    isEqual,
                    isEqual ? string.Empty : $"Deserialized value at index {i}, \"{right}\", must equal original value, \"{left}\"");
            }

            Assert.Equal(writer.Position, reader.Position);
            Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
        }

        /// <summary>
        /// Checks if values can be round-tripped when used as a field in a tuple.
        /// </summary>
        [Fact]
        public void CanRoundTripTupleViaSerializer()
        {
            var serializer = _serviceProvider.GetRequiredService<Serializer<(string, TValue, TValue, string)>>();

            var original = (Guid.NewGuid().ToString(), CreateValue(), CreateValue(), Guid.NewGuid().ToString());
            var originalCopy = (original.Item1, GetWriteCopy(original.Item2), GetWriteCopy(original.Item3), original.Item4);

            using var writerSession = _sessionPool.GetSession();
            var writer = Writer.CreatePooled(writerSession);
            serializer.Serialize(originalCopy, ref writer);
            using var readerSession = _sessionPool.GetSession();
            var reader = Reader.Create(writer.Output, readerSession);
            var deserialized = serializer.Deserialize(ref reader);

            var isEqual = Equals(original.Item1, deserialized.Item1);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Deserialized value for item 1, \"{deserialized.Item1}\", must equal original value, \"{original.Item1}\"");
            isEqual = Equals(original.Item2, deserialized.Item2);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Deserialized value for item 2, \"{deserialized.Item2}\", must equal original value, \"{original.Item2}\"");
            isEqual = Equals(original.Item3, deserialized.Item3);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Deserialized value for item 3, \"{deserialized.Item3}\", must equal original value, \"{original.Item3}\"");
            isEqual = Equals(original.Item4, deserialized.Item4);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Deserialized value for item 4, \"{deserialized.Item4}\", must equal original value, \"{original.Item4}\"");

            Assert.Equal(writer.Position, reader.Position);
            Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
        }

        /// <summary>
        /// Checks if values can be round-tripped through <see cref="Serializer{T}"/> when using <typeparamref name="TValue"/> as the type parameter.
        /// </summary>
        [Fact]
        public void CanRoundTripViaSerializer()
        {
            var serializer = _serviceProvider.GetRequiredService<Serializer<TValue>>();

            foreach (var original in TestValues)
            {
                Test(original);
            }

            if (ValueProvider is { } valueProvider)
            {
                valueProvider(Test);
            }

            void Test(TValue original)
            {
                var toWrite = GetWriteCopy(original);
                var buffer = new TestMultiSegmentBufferWriter(1024);
                using var writerSession = _sessionPool.GetSession();
                var writer = Writer.Create(buffer, writerSession);
                serializer.Serialize(toWrite, ref writer);
                using var readerSession = _sessionPool.GetSession();
                var reader = Reader.Create(buffer.GetReadOnlySequence(0), readerSession);
                var deserialized = serializer.Deserialize(ref reader);

                var isEqual = Equals(original, deserialized);
                Assert.True(
                    isEqual,
                    isEqual ? string.Empty : $"Deserialized value \"{deserialized}\" must equal original value \"{original}\"");
                Assert.Equal(writer.Position, reader.Position);
                Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
            }
        }

        /// <summary>
        /// Checks if values can be round-tripped through <see cref="Serializer{T}"/> when using <see cref="object"/> as the type parameter.
        /// </summary>
        [Fact]
        public void CanRoundTripViaObjectSerializer()
        {
            var serializer = _serviceProvider.GetRequiredService<Serializer<object>>();

            var buffer = new byte[10240];

            foreach (var original in TestValues)
            {
                buffer.AsSpan().Clear();
                var toWrite = GetWriteCopy(original);
                using var writerSession = _sessionPool.GetSession();
                var writer = Writer.Create(buffer, writerSession);
                serializer.Serialize(toWrite, ref writer);

                using var readerSession = _sessionPool.GetSession();
                var reader = Reader.Create(buffer, readerSession);
                var deserializedObject = serializer.Deserialize(ref reader);
                if (original != null && !typeof(TValue).IsEnum)
                {
                    var deserialized = Assert.IsAssignableFrom<TValue>(deserializedObject);
                    var isEqual = Equals(original, deserialized);
                    Assert.True(
                        isEqual,
                        isEqual ? string.Empty : $"Deserialized value \"{deserialized}\" must equal original value \"{original}\"");
                }
                else if (typeof(TValue).IsEnum)
                {
                    var deserialized = (TValue)deserializedObject;
                    var isEqual = Equals(original, deserialized);
                    Assert.True(
                        isEqual,
                        isEqual ? string.Empty : $"Deserialized value \"{deserialized}\" must equal original value \"{original}\"");
                }
                else
                {
                    Assert.Null(deserializedObject);
                }

                Assert.Equal(writer.Position, reader.Position);
                Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
            }
        }

        /// <summary>
        /// Checks if round-tripped values are equal.
        /// </summary>
        [Fact]
        public void RoundTrippedValuesEqual() => TestRoundTrippedValue(CreateValue());

        /// <summary>
        /// Checks if round-tripped default values are equal.
        /// </summary>
        [Fact]
        public void CanRoundTripDefaultValueViaCodec() => TestRoundTrippedValue(default);

        /// <summary>
        /// Checks if values can be skipped over.
        /// </summary>
        [Fact]
        public void CanSkipValue() => CanBeSkipped(default);

        /// <summary>
        /// Checks if default values can be skipped over.
        /// </summary>
        [Fact]
        public void CanSkipDefaultValue() => CanBeSkipped(default);

        /// <summary>
        /// Checks if buffers are handled correctly.
        /// </summary>
        [Fact]
        public void CorrectlyHandlesBuffers()
        {
            var testers = BufferTestHelper<TValue>.GetTestSerializers(_serviceProvider, MaxSegmentSizes);

            foreach (var tester in testers)
            {
                foreach (var maxSegmentSize in MaxSegmentSizes)
                {
                    foreach (var value in TestValues)
                    {
                        var toWrite = GetWriteCopy(value);
                        var buffer = tester.Serialize(toWrite);
                        var sequence = buffer.GetReadOnlySequence(maxSegmentSize);
                        tester.Deserialize(sequence, out var output);
                        var bufferWriterType = tester.GetType().BaseType?.GenericTypeArguments[1];
                        var isEqual = Equals(value, output);
                        Assert.True(isEqual,
                            isEqual ? string.Empty : $"Deserialized value {output} must be equal to serialized value {value}. " +
                            $"IBufferWriter<> type: {bufferWriterType}, Max Read Segment Size: {maxSegmentSize}. " +
                            $"Buffer: 0x{string.Join(" ", sequence.ToArray().Select(b => $"{b:X2}"))}");
                    }
                }
            }
        }

        private void CanBeSkipped(TValue original)
        {
            var pipe = new Pipe();
            using var writerSession = _sessionPool.GetSession();
            var writer = Writer.Create(pipe.Writer, writerSession);
            var writerCodec = CreateCodec();
            var toWrite = GetWriteCopy(original);
            writerCodec.WriteField(ref writer, 0, typeof(TValue), toWrite);
            var expectedLength = writer.Position;
            writer.Commit();
            _ = pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            pipe.Writer.Complete();

            _ = pipe.Reader.TryRead(out var readResult);

            {
                using var readerSession = _sessionPool.GetSession();
                var reader = Reader.Create(readResult.Buffer, readerSession);
                var readField = reader.ReadFieldHeader();
                reader.SkipField(readField);
                Assert.Equal(expectedLength, reader.Position);
                Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
            }

            {
                var codec = new SkipFieldCodec();
                using var readerSession = _sessionPool.GetSession();
                var reader = Reader.Create(readResult.Buffer, readerSession);
                var readField = reader.ReadFieldHeader();
                var shouldBeNull = codec.ReadValue(ref reader, readField);
                Assert.Null(shouldBeNull);
                Assert.Equal(expectedLength, reader.Position);
                Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
            }

            {
                using var readerSession = _sessionPool.GetSession();
                var reader = Reader.Create(readResult.Buffer, readerSession);
                var readField = reader.ReadFieldHeader();
                reader.ConsumeUnknownField(readField);
                Assert.Equal(expectedLength, reader.Position);
                Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
            }

            pipe.Reader.AdvanceTo(readResult.Buffer.End);
            pipe.Reader.Complete();
        }

        private void TestRoundTrippedValue(TValue original)
        {
            var pipe = new Pipe();
            using var writerSession = _sessionPool.GetSession();
            var writer = Writer.Create(pipe.Writer, writerSession);
            var writerCodec = CreateCodec();
            writerCodec.WriteField(ref writer, 0, typeof(TValue), GetWriteCopy(original));
            writer.Commit();
            _ = pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            pipe.Writer.Complete();

            _ = pipe.Reader.TryRead(out var readResult);
            using var readerSession = _sessionPool.GetSession();
            var reader = Reader.Create(readResult.Buffer, readerSession);
            var readerCodec = CreateCodec();
            var readField = reader.ReadFieldHeader();
            var deserialized = readerCodec.ReadValue(ref reader, readField);
            pipe.Reader.AdvanceTo(readResult.Buffer.End);
            pipe.Reader.Complete();
            var isEqual = Equals(original, deserialized);
            Assert.True(
                isEqual,
                isEqual ? string.Empty : $"Deserialized value \"{deserialized}\" must equal original value \"{original}\"");
            Assert.Equal(writerSession.ReferencedObjects.CurrentReferenceId, readerSession.ReferencedObjects.CurrentReferenceId);
        }

        /// <summary>
        /// Round-trips a value through the codec.
        /// </summary>
        protected T RoundTripThroughCodec<T>(T original)
        {
            T result;
            using (var readerSession = SessionPool.GetSession())
            using (var writeSession = SessionPool.GetSession())
            {
                var writer = Writer.CreatePooled(writeSession);
                try
                {
                    var codec = ServiceProvider.GetRequiredService<ICodecProvider>().GetCodec<T>();
                    codec.WriteField(
                        ref writer,
                        0,
                        null,
                        original);
                    writer.Commit();

                    var output = writer.Output.AsReadOnlySequence();
                    var reader = Reader.Create(output, readerSession);

                    var previousPos = reader.Position;
                    var initialHeader = reader.ReadFieldHeader();
                    Assert.True(reader.Position > previousPos);

                    result = codec.ReadValue(ref reader, initialHeader);
                }
                finally
                {
                    writer.Dispose();
                }
            }

            return result;
        }

        /// <summary>
        /// Round-trips a value through an untyped serializer.
        /// </summary>
        protected object RoundTripThroughUntypedSerializer(object original, out string formattedBitStream)
        {
            object result;
            using (var readerSession = SessionPool.GetSession())
            using (var writeSession = SessionPool.GetSession())
            {
                var writer = Writer.CreatePooled(writeSession);
                try
                {
                    var serializer = ServiceProvider.GetService<Serializer<object>>();
                    serializer.Serialize(original, ref writer);

                    using var analyzerSession = SessionPool.GetSession();
                    var output = writer.Output.Slice();
                    formattedBitStream = BitStreamFormatter.Format(output, analyzerSession);

                    var reader = Reader.Create(output, readerSession);

                    result = serializer.Deserialize(ref reader);
                }
                finally
                {
                    writer.Dispose();
                }
            }

            return result;
        }
    }
}