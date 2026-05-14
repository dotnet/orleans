using CsCheck;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using Orleans.Serialization.Session;
using Orleans.Serialization.TestKit;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Xunit;
using Xunit.Abstractions;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;
using System.Runtime.InteropServices;

namespace Orleans.Serialization.UnitTests
{
    [Trait("Category", "BVT")]
    public sealed class ReaderTests
    {
        [Theory]
        [InlineData(0x01, 1)]
        [InlineData(0x02, 2)]
        [InlineData(0x04, 3)]
        [InlineData(0x08, 4)]
        [InlineData(0x10, 5)]
        [InlineData(0x20, 6)]
        [InlineData(0x00, 9)]
        public void GetVarIntByteCount_ReturnsEncodedByteCount(byte firstByte, int expected)
        {
            Assert.Equal(expected, Reader.GetVarIntByteCount(firstByte));
        }

        [Fact]
        public void PeekByte_DoesNotAdvanceReader()
        {
            var reader = Reader.Create(new byte[] { 0x12, 0x34 }, session: null!);

            Assert.Equal(0, reader.Position);
            Assert.Equal(0x12, reader.PeekByte());
            Assert.Equal(0, reader.Position);
            Assert.Equal(0x12, reader.ReadByte());
            Assert.Equal(1, reader.Position);
        }

        [Fact]
        public void ReadVarUInt32_RejectsOverflowBits()
        {
            var bytes = WriteVarUInt32(uint.MaxValue);
            bytes[^1] |= 0xE0;

            Assert.Throws<OverflowException>(() => ReadVarUInt32(bytes));
        }

        [Fact]
        public void ReadVarUInt64_IgnoresFollowingByteAfterNineByteValue()
        {
            var bytes = WriteVarUInt64(1UL << 62);
            Array.Resize(ref bytes, bytes.Length + 1);
            bytes[^1] = 0x01;

            var reader = Reader.Create(bytes, session: null!);

            Assert.Equal(1UL << 62, reader.ReadVarUInt64());
            Assert.Equal(0x01, reader.ReadByte());
        }

        [Fact]
        public void ReadVarUInt64_RejectsOverflowBits()
        {
            var bytes = WriteVarUInt64(ulong.MaxValue);
            bytes[^1] |= 0xFC;

            Assert.Throws<OverflowException>(() => ReadVarUInt64(bytes));
            Assert.Throws<OverflowException>(() => ReadVarUInt64FromStream(bytes));
        }

        private static uint ReadVarUInt32(byte[] bytes)
        {
            var reader = Reader.Create(bytes, session: null!);
            return reader.ReadVarUInt32();
        }

        private static ulong ReadVarUInt64(byte[] bytes)
        {
            var reader = Reader.Create(bytes, session: null!);
            return reader.ReadVarUInt64();
        }

        private static ulong ReadVarUInt64FromStream(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            var reader = Reader.Create(stream, session: null!);
            return reader.ReadVarUInt64();
        }

        private static byte[] WriteVarUInt32(uint value)
        {
            var output = new ArrayBufferWriter<byte>();
            var writer = Writer.Create(output, session: null!);
            writer.WriteVarUInt32(value);
            writer.Commit();
            return output.WrittenSpan.ToArray();
        }

        private static byte[] WriteVarUInt64(ulong value)
        {
            var output = new ArrayBufferWriter<byte>();
            var writer = Writer.Create(output, session: null!);
            writer.WriteVarUInt64(value);
            writer.Commit();
            return output.WrittenSpan.ToArray();
        }
    }

    /// <summary>
    /// Tests for Orleans' low-level Reader and Writer implementations.
    /// 
    /// These tests verify the fundamental building blocks of Orleans serialization:
    /// - Binary encoding/decoding of primitive types
    /// - Variable-length integer encoding (VarInt) for space efficiency
    /// - Buffer management and pooling strategies
    /// - Stream-based and memory-based I/O operations
    /// 
    /// The Reader/Writer infrastructure provides:
    /// - High-performance binary serialization primitives
    /// - Zero-allocation patterns for common scenarios
    /// - Support for various buffer types (streams, arrays, pipes)
    /// - Efficient handling of large data through segmented buffers
    /// 
    /// These components are critical for Orleans' wire protocol efficiency
    /// and directly impact the performance of grain communication.
    /// </summary>
    [Trait("Category", "BVT")]
    public sealed class ReaderWriterPoolingStreamTest : ReaderWriterTestBase<Stream, PoolingStreamBufferWriter, ReaderInput>
    {
        public ReaderWriterPoolingStreamTest(ITestOutputHelper output) : base(output)
        {
        }

        protected override Stream CreateBuffer() => new MemoryStream();
        protected override Reader<ReaderInput> CreateReader(Stream buffer, SerializerSession session)
        {
            buffer.Position = 0;
            return Reader.Create(buffer, session);
        }

        protected override Writer<PoolingStreamBufferWriter> CreateWriter(Stream buffer, SerializerSession session) => Writer.CreatePooled(buffer, session);
        protected override Stream GetBuffer(Stream originalBuffer, PoolingStreamBufferWriter output) => originalBuffer;
        protected override void DisposeBuffer(Stream buffer, PoolingStreamBufferWriter output)
        {
            output.Dispose();
            buffer.Dispose();
        }

        [Fact]
        public override void VarUInt32RoundTrip() => VarUInt32RoundTripTest();

        [Fact]
        public override void VarUInt64RoundTrip() => VarUInt64RoundTripTest();

        [Fact]
        public override void Int64RoundTrip() => Int64RoundTripTest();

        [Fact]
        public override void Int32RoundTrip() => Int32RoundTripTest();

        [Fact]
        public override void UInt64RoundTrip() => UInt64RoundTripTest();

        [Fact]
        public override void UInt32RoundTrip() => UInt32RoundTripTest();

        [Fact]
        protected override void ByteRoundTrip() => ByteRoundTripTest();
    }

    [Trait("Category", "BVT")]
    public sealed class ReaderWriterStreamTest : ReaderWriterTestBase<Stream, ArrayStreamBufferWriter, ReaderInput>
    {
        public ReaderWriterStreamTest(ITestOutputHelper output) : base(output)
        {
        }

        protected override Stream CreateBuffer() => new MemoryStream();
        protected override Reader<ReaderInput> CreateReader(Stream buffer, SerializerSession session)
        {
            buffer.Position = 0;
            return Reader.Create(buffer, session);
        }

        protected override Writer<ArrayStreamBufferWriter> CreateWriter(Stream buffer, SerializerSession session) => Writer.Create(buffer, session);
        protected override Stream GetBuffer(Stream originalBuffer, ArrayStreamBufferWriter output) => originalBuffer;
        protected override void DisposeBuffer(Stream buffer, ArrayStreamBufferWriter output) => buffer.Dispose();

        [Fact]
        public override void VarUInt32RoundTrip() => VarUInt32RoundTripTest();

        [Fact]
        public override void VarUInt64RoundTrip() => VarUInt64RoundTripTest();

        [Fact]
        public override void Int64RoundTrip() => Int64RoundTripTest();

        [Fact]
        public override void Int32RoundTrip() => Int32RoundTripTest();

        [Fact]
        public override void UInt64RoundTrip() => UInt64RoundTripTest();

        [Fact]
        public override void UInt32RoundTrip() => UInt32RoundTripTest();

        [Fact]
        protected override void ByteRoundTrip() => ByteRoundTripTest();
    }

    [Trait("Category", "BVT")]
    public sealed class ReaderWriterMemoryStreamTest : ReaderWriterTestBase<MemoryStream, MemoryStreamBufferWriter, ReaderInput>
    {
        public ReaderWriterMemoryStreamTest(ITestOutputHelper output) : base(output)
        {
        }

        protected override MemoryStream CreateBuffer() => new();
        protected override Reader<ReaderInput> CreateReader(MemoryStream buffer, SerializerSession session)
        {
            buffer.Position = 0;
            return Reader.Create(buffer, session);
        }

        protected override Writer<MemoryStreamBufferWriter> CreateWriter(MemoryStream buffer, SerializerSession session) => Writer.Create(buffer, session);
        protected override MemoryStream GetBuffer(MemoryStream originalBuffer, MemoryStreamBufferWriter output) => originalBuffer;
        protected override void DisposeBuffer(MemoryStream buffer, MemoryStreamBufferWriter output) => buffer.Dispose();

        [Fact]
        public override void VarUInt32RoundTrip() => VarUInt32RoundTripTest();

        [Fact]
        public override void VarUInt64RoundTrip() => VarUInt64RoundTripTest();

        [Fact]
        public override void Int64RoundTrip() => Int64RoundTripTest();

        [Fact]
        public override void Int32RoundTrip() => Int32RoundTripTest();

        [Fact]
        public override void UInt64RoundTrip() => UInt64RoundTripTest();

        [Fact]
        public override void UInt32RoundTrip() => UInt32RoundTripTest();

        [Fact]
        protected override void ByteRoundTrip() => ByteRoundTripTest();
    }

    [Trait("Category", "BVT")]
    public sealed class ReaderWriterSpanTest : ReaderWriterTestBase<byte[], SpanBufferWriter, SpanReaderInput>
    {
        public ReaderWriterSpanTest(ITestOutputHelper output) : base(output)
        {
        }

        protected override byte[] CreateBuffer() => new byte[100];
        protected override Reader<SpanReaderInput> CreateReader(byte[] buffer, SerializerSession session) => Reader.Create(buffer, session);
        protected override Writer<SpanBufferWriter> CreateWriter(byte[] buffer, SerializerSession session) => Writer.Create(buffer, session);
        protected override byte[] GetBuffer(byte[] originalBuffer, SpanBufferWriter output) => originalBuffer;
        protected override void DisposeBuffer(byte[] buffer, SpanBufferWriter output)
        {
        }

        [Fact]
        public override void VarUInt32RoundTrip() => VarUInt32RoundTripTest();

        [Fact]
        public override void VarUInt64RoundTrip() => VarUInt64RoundTripTest();

        [Fact]
        public override void Int64RoundTrip() => Int64RoundTripTest();

        [Fact]
        public override void Int32RoundTrip() => Int32RoundTripTest();

        [Fact]
        public override void UInt64RoundTrip() => UInt64RoundTripTest();

        [Fact]
        public override void UInt32RoundTrip() => UInt32RoundTripTest();

        [Fact]
        protected override void ByteRoundTrip() => ByteRoundTripTest();
    }

    [Trait("Category", "BVT")]
    public sealed class ReaderWriterSegmentWriterTest : ReaderWriterTestBase<TestMultiSegmentBufferWriter, TestMultiSegmentBufferWriter, ReadOnlySequenceInput>
    {
        public ReaderWriterSegmentWriterTest(ITestOutputHelper output) : base(output)
        {
        }

        protected override TestMultiSegmentBufferWriter CreateBuffer() => new(maxAllocationSize: 10);
        protected override Reader<ReadOnlySequenceInput> CreateReader(TestMultiSegmentBufferWriter buffer, SerializerSession session) => Reader.Create(buffer.GetReadOnlySequence(maxSegmentSize: 8), session);
        protected override Writer<TestMultiSegmentBufferWriter> CreateWriter(TestMultiSegmentBufferWriter buffer, SerializerSession session) => Writer.Create(buffer, session);
        protected override TestMultiSegmentBufferWriter GetBuffer(TestMultiSegmentBufferWriter originalBuffer, TestMultiSegmentBufferWriter output) => output;
        protected override void DisposeBuffer(TestMultiSegmentBufferWriter buffer, TestMultiSegmentBufferWriter output)
        {
        }

        [Fact]
        public override void VarUInt32RoundTrip() => VarUInt32RoundTripTest();

        [Fact]
        public override void VarUInt64RoundTrip() => VarUInt64RoundTripTest();

        [Fact]
        public override void Int64RoundTrip() => Int64RoundTripTest();

        [Fact]
        public override void Int32RoundTrip() => Int32RoundTripTest();

        [Fact]
        public override void UInt64RoundTrip() => UInt64RoundTripTest();

        [Fact]
        public override void UInt32RoundTrip() => UInt32RoundTripTest();

        [Fact]
        protected override void ByteRoundTrip() => ByteRoundTripTest();

        [Fact]
        public void SkipBufferEdge_ReadOnlySequence()
        {
            byte[] b = new byte[] { 25, 84, 101, 115, 116, 32, 97, 99, 99, 111, 117, 110 };
            byte[] b2 = new byte[] { 116, 64, 0, 0, 0 };

            var seq = ReadOnlySequenceHelper.CreateReadOnlySequence(b, b2);
            using SerializerSession session = this.GetSession();
            var reader = Reader.Create(seq, session);
            SkipFieldExtension.SkipField(ref reader, new Field(new Tag((byte)WireType.LengthPrefixed)));

            Assert.Equal(64, reader.ReadInt32());
        }

        [Fact]
        public void SkipBufferEdge_BufferSlice()
        {
            byte[] b = new byte[] { 25, 84, 101, 115, 116, 32, 97, 99, 99, 111, 117, 110 };
            byte[] b2 = new byte[] { 116, 64, 0, 0, 0 };

            var buffer = new PooledBuffer();

            // PooledBuffer / BufferSlice is more abstract than ReadOnlySequence, which is why we are relying on 
            // implementation details.
            var buf = buffer.GetMemory(1);
            Assert.True(MemoryMarshal.TryGetArray<byte>(buf, out var seg));
            var offset = seg.Array.Length - b.Length;
            buffer.Write(new byte[offset]);
            buffer.Write(b);
            buffer.Write(b2);
            var slice = buffer.Slice(offset);

            // Verify that the slices are what we expect.
            var count = 0;
            foreach (var s in slice)
            {
                if (count == 0)
                {
                    Assert.Equal(b, s.ToArray());
                }
                else
                {
                    Assert.Equal(b2, s.ToArray());
                }

                ++count;
            }

            Assert.Equal(2, count);

            using SerializerSession session = this.GetSession();
            var reader = Reader.Create(slice, session);
            SkipFieldExtension.SkipField(ref reader, new Field(new Tag((byte)WireType.LengthPrefixed)));

            Assert.Equal(64, reader.ReadInt32());
            buffer.Dispose();
        }
    }

    public abstract class ReaderWriterTestBase<TBuffer, TOutput, TInput> where TOutput : IBufferWriter<byte>
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly SerializerSessionPool _sessionPool;
        private readonly ITestOutputHelper _testOutputHelper;

        private delegate T ReadValue<T>(ref Reader<TInput> reader);
        private delegate void WriteValue<T>(ref Writer<TOutput> writer, T value);

        public ReaderWriterTestBase(ITestOutputHelper testOutputHelper)
        {
            var services = new ServiceCollection();
            _ = services.AddSerializer();
            _serviceProvider = services.BuildServiceProvider();
            _sessionPool = _serviceProvider.GetService<SerializerSessionPool>();
            _testOutputHelper = testOutputHelper;
        }

        protected SerializerSession GetSession() => _sessionPool.GetSession();
        protected abstract TBuffer CreateBuffer();
        protected abstract Reader<TInput> CreateReader(TBuffer buffer, SerializerSession session);
        protected abstract Writer<TOutput> CreateWriter(TBuffer buffer, SerializerSession session);
        protected abstract TBuffer GetBuffer(TBuffer originalBuffer, TOutput output);
        protected abstract void DisposeBuffer(TBuffer buffer, TOutput output);

        private Func<T, bool> CreateTestPredicate<T>(WriteValue<T> writeValue, ReadValue<T> readValue)
        {
            return Test;

            bool Test(T expected)
            {
                var buffer = CreateBuffer();
                using var writerSession = _sessionPool.GetSession();
                var writer = CreateWriter(buffer, writerSession);
                try
                {
                    for (int i = 0; i < 5; i++)
                    {
                        writeValue(ref writer, expected);
                    }

                    writer.Commit();
                    using var readerSession = _sessionPool.GetSession();
                    var readerBuffer = GetBuffer(buffer, writer.Output);
                    var reader = CreateReader(readerBuffer, readerSession);

                    for (int i = 0; i < 5; i++)
                    {
                        var actual = readValue(ref reader);
                        if (!EqualityComparer<T>.Default.Equals(expected, actual))
                        {
                            _testOutputHelper.WriteLine(
                                $"Failure: Actual: \"{actual}\" (0x{actual:X}). Expected \"{expected}\" (0x{expected:X}). Iteration: {i}");
                            return false;
                        }
                    }

                    return true;
                }
                finally
                {
                    var disposeBuffer = GetBuffer(buffer, writer.Output);
                    DisposeBuffer(disposeBuffer, writer.Output);
                }
            }
        }

        public abstract void VarUInt32RoundTrip();
        public abstract void VarUInt64RoundTrip();
        public abstract void Int64RoundTrip();
        public abstract void Int32RoundTrip();
        public abstract void UInt64RoundTrip();
        public abstract void UInt32RoundTrip();
        protected abstract void ByteRoundTrip();

        protected void VarUInt32RoundTripTest()
        {
            static uint Read(ref Reader<TInput> reader) => reader.ReadVarUInt32();
            static void Write(ref Writer<TOutput> writer, uint expected) => writer.WriteVarUInt32(expected);

            Gen.UInt.Sample(CreateTestPredicate(Write, Read));
        }

        protected void VarUInt64RoundTripTest()
        {
            static ulong Read(ref Reader<TInput> reader) => reader.ReadVarUInt64();
            static void Write(ref Writer<TOutput> writer, ulong expected) => writer.WriteVarUInt64(expected);

            Gen.ULong.Sample(CreateTestPredicate(Write, Read));
        }

        protected void Int64RoundTripTest()
        {
            static long Read(ref Reader<TInput> reader) => reader.ReadInt64();
            static void Write(ref Writer<TOutput> writer, long expected) => writer.WriteInt64(expected);

            Gen.Long.Sample(CreateTestPredicate(Write, Read));

        }

        protected void Int32RoundTripTest()
        {
            static int Read(ref Reader<TInput> reader) => reader.ReadInt32();
            static void Write(ref Writer<TOutput> writer, int expected) => writer.WriteInt32(expected);

            Gen.Int.Sample(CreateTestPredicate(Write, Read));
        }

        protected void UInt64RoundTripTest()
        {
            static ulong Read(ref Reader<TInput> reader) => reader.ReadUInt64();
            static void Write(ref Writer<TOutput> writer, ulong expected) => writer.WriteUInt64(expected);

            Gen.ULong.Sample(CreateTestPredicate(Write, Read));
        }

        protected void UInt32RoundTripTest()
        {
            static uint Read(ref Reader<TInput> reader) => reader.ReadUInt32();
            static void Write(ref Writer<TOutput> writer, uint expected) => writer.WriteUInt32(expected);

            Gen.UInt.Sample(CreateTestPredicate(Write, Read));
        }

        protected void ByteRoundTripTest()
        {
            static byte Read(ref Reader<TInput> reader) => reader.ReadByte();
            static void Write(ref Writer<TOutput> writer, byte expected) => writer.WriteByte(expected);

            Gen.Byte.Sample(CreateTestPredicate(Write, Read));
        }
    }
}
