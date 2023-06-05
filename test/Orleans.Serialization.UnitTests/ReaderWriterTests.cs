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

namespace Orleans.Serialization.UnitTests
{
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
