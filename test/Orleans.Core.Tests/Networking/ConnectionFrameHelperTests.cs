using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Orleans.Runtime.Messaging;
using Xunit;

namespace Orleans.Core.Tests.Networking
{
    [Trait("Category", "BVT")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class ConnectionFrameHelperTests
    {
        [Fact]
        public async Task WriteAndReadFrame_RoundTrips()
        {
            var pipe = new Pipe();
            var context = new TestConnectionContext(pipe);

            byte expectedFrameType = 0x01;
            byte[] expectedPayload = Encoding.UTF8.GetBytes("hello world");

            await ConnectionFrameHelper.WriteFrameAsync(context, expectedFrameType, expectedPayload, CancellationToken.None);
            await pipe.Writer.CompleteAsync();

            var (frameType, payload) = await ConnectionFrameHelper.ReadFrameAsync(context, CancellationToken.None);

            Assert.Equal(expectedFrameType, frameType);
            Assert.Equal(expectedPayload, payload);
        }

        [Fact]
        public async Task WriteAndReadFrame_EmptyPayload()
        {
            var pipe = new Pipe();
            var context = new TestConnectionContext(pipe);

            byte expectedFrameType = 0x05;
            byte[] expectedPayload = Array.Empty<byte>();

            await ConnectionFrameHelper.WriteFrameAsync(context, expectedFrameType, expectedPayload, CancellationToken.None);
            await pipe.Writer.CompleteAsync();

            var (frameType, payload) = await ConnectionFrameHelper.ReadFrameAsync(context, CancellationToken.None);

            Assert.Equal(expectedFrameType, frameType);
            Assert.Empty(payload);
        }

        [Fact]
        public async Task WriteAndReadFrame_ZeroCopyPath_RoundTrips()
        {
            var pipe = new Pipe();
            var context = new TestConnectionContext(pipe);

            byte expectedFrameType = 0x02;
            var expectedText = "zero-copy test";

            await ConnectionFrameHelper.WriteFrameAsync(
                context,
                expectedFrameType,
                writer => { writer.Write(Encoding.UTF8.GetBytes(expectedText)); },
                CancellationToken.None);

            await pipe.Writer.CompleteAsync();

            var (frameType, payload) = await ConnectionFrameHelper.ReadFrameAsync(context, CancellationToken.None);

            Assert.Equal(expectedFrameType, frameType);
            Assert.Equal(expectedText, Encoding.UTF8.GetString(payload));
        }

        [Fact]
        public async Task WriteFrame_CompletedReader_Throws()
        {
            var pipe = new Pipe();
            var context = new TestConnectionContext(pipe);
            await pipe.Reader.CompleteAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await ConnectionFrameHelper.WriteFrameAsync(context, 0x01, Array.Empty<byte>(), CancellationToken.None));
        }

        [Fact]
        public async Task WriteFrame_ZeroCopyPath_CompletedReader_Throws()
        {
            var pipe = new Pipe();
            var context = new TestConnectionContext(pipe);
            await pipe.Reader.CompleteAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await ConnectionFrameHelper.WriteFrameAsync(context, 0x01, _ => { }, CancellationToken.None));
        }

        [Fact]
        public async Task ReadFrame_RejectsFrameExceedingMaxLength()
        {
            var pipe = new Pipe();
            var context = new TestConnectionContext(pipe);

            var output = pipe.Writer;
            var lengthBytes = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, 2_000_000);
            output.Write(lengthBytes);
            await output.FlushAsync(TestContext.Current.CancellationToken);
            await output.CompleteAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await ConnectionFrameHelper.ReadFrameAsync(context, CancellationToken.None, maxFrameLength: 1024));
        }

        [Fact]
        public async Task ReadFrame_RejectsInvalidFrameLength()
        {
            var pipe = new Pipe();
            var context = new TestConnectionContext(pipe);

            var output = pipe.Writer;
            var lengthBytes = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, 0);
            output.Write(lengthBytes);
            await output.FlushAsync(TestContext.Current.CancellationToken);
            await output.CompleteAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await ConnectionFrameHelper.ReadFrameAsync(context, CancellationToken.None));
        }

        [Fact]
        public async Task WriteAndReadMultipleFrames()
        {
            var pipe = new Pipe();
            var context = new TestConnectionContext(pipe);

            await ConnectionFrameHelper.WriteFrameAsync(context, 0x01, Encoding.UTF8.GetBytes("first"), CancellationToken.None);
            await ConnectionFrameHelper.WriteFrameAsync(context, 0x02, Encoding.UTF8.GetBytes("second"), CancellationToken.None);
            await ConnectionFrameHelper.WriteFrameAsync(context, 0x03, Encoding.UTF8.GetBytes("third"), CancellationToken.None);
            await pipe.Writer.CompleteAsync();

            var (type1, payload1) = await ConnectionFrameHelper.ReadFrameAsync(context, CancellationToken.None);
            var (type2, payload2) = await ConnectionFrameHelper.ReadFrameAsync(context, CancellationToken.None);
            var (type3, payload3) = await ConnectionFrameHelper.ReadFrameAsync(context, CancellationToken.None);

            Assert.Equal(0x01, type1);
            Assert.Equal("first", Encoding.UTF8.GetString(payload1));
            Assert.Equal(0x02, type2);
            Assert.Equal("second", Encoding.UTF8.GetString(payload2));
            Assert.Equal(0x03, type3);
            Assert.Equal("third", Encoding.UTF8.GetString(payload3));
        }

        [Fact]
        public async Task ReadFrame_ConsumesDataUnderBackpressure()
        {
            var pipe = new Pipe(new PipeOptions(
                pauseWriterThreshold: 64,
                resumeWriterThreshold: 32,
                minimumSegmentSize: 16,
                useSynchronizationContext: false));
            var context = new TestConnectionContext(pipe);
            var expectedPayload = new byte[256];
            Array.Fill(expectedPayload, (byte)0x2A);

            var writeTask = WriteFrameInChunksAsync(pipe.Writer, 0x04, expectedPayload, 16);
            var readTask = ConnectionFrameHelper.ReadFrameAsync(context, CancellationToken.None).AsTask();

            await Task.WhenAll(writeTask, readTask).WaitAsync(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);

            var (frameType, payload) = await readTask;
            Assert.Equal(0x04, frameType);
            Assert.Equal(expectedPayload, payload);
        }

        [Fact]
        public void WriteLengthPrefixedString_ReadLengthPrefixedString_RoundTrips()
        {
            var buffer = new ArrayBufferWriter<byte>();
            var expected = "hello, orleans!";

            ConnectionFrameHelper.WriteLengthPrefixedString(buffer, expected);

            var data = buffer.WrittenSpan.ToArray();
            int offset = 0;
            var result = ConnectionFrameHelper.ReadLengthPrefixedString(data, ref offset);

            Assert.Equal(expected, result);
            Assert.Equal(data.Length, offset);
        }

        [Fact]
        public void WriteLengthPrefixedString_EmptyString()
        {
            var buffer = new ArrayBufferWriter<byte>();

            ConnectionFrameHelper.WriteLengthPrefixedString(buffer, "");

            var data = buffer.WrittenSpan.ToArray();
            int offset = 0;
            var result = ConnectionFrameHelper.ReadLengthPrefixedString(data, ref offset);

            Assert.Equal("", result);
            Assert.Equal(4, offset);
        }

        [Fact]
        public void WriteLengthPrefixedString_MultipleStrings()
        {
            var buffer = new ArrayBufferWriter<byte>();
            var strings = new[] { "alpha", "beta", "gamma" };

            foreach (var s in strings)
                ConnectionFrameHelper.WriteLengthPrefixedString(buffer, s);

            var data = buffer.WrittenSpan.ToArray();
            int offset = 0;
            foreach (var expected in strings)
            {
                var result = ConnectionFrameHelper.ReadLengthPrefixedString(data, ref offset);
                Assert.Equal(expected, result);
            }
        }

        [Fact]
        public void ReadLengthPrefixedString_InsufficientData_Throws()
        {
            var data = new byte[] { 0x01, 0x02 };
            int offset = 0;

            Assert.Throws<InvalidOperationException>(() =>
                ConnectionFrameHelper.ReadLengthPrefixedString(data, ref offset));
        }

        [Fact]
        public void ReadLengthPrefixedString_OversizedLength_Throws()
        {
            var data = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(data, int.MaxValue);
            int offset = 0;

            Assert.Throws<InvalidOperationException>(() =>
                ConnectionFrameHelper.ReadLengthPrefixedString(data, ref offset));
        }

        [Fact]
        public void ReadLengthPrefixedString_NegativeOffset_Throws()
        {
            var data = Array.Empty<byte>();
            int offset = -1;

            Assert.Throws<InvalidOperationException>(() =>
                ConnectionFrameHelper.ReadLengthPrefixedString(data, ref offset));
        }

        private static async Task WriteFrameInChunksAsync(PipeWriter writer, byte frameType, byte[] payload, int chunkSize)
        {
            var frame = new byte[ConnectionFrameHelper.FramePrefixSize + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length + 1);
            frame[4] = frameType;
            payload.CopyTo(frame, ConnectionFrameHelper.FramePrefixSize);

            for (var offset = 0; offset < frame.Length; offset += chunkSize)
            {
                var length = Math.Min(chunkSize, frame.Length - offset);
                writer.Write(frame.AsSpan(offset, length));
                await writer.FlushAsync();
            }

            await writer.CompleteAsync();
        }

        /// <summary>
        /// Minimal ConnectionContext backed by a Pipe for testing.
        /// </summary>
        private sealed class TestConnectionContext : ConnectionContext
        {
            private readonly IDuplexPipe _transport;

            public TestConnectionContext(Pipe pipe)
            {
                _transport = new DuplexPipe(pipe.Reader, pipe.Writer);
            }

            public override string ConnectionId { get; set; } = Guid.NewGuid().ToString();
            public override IDuplexPipe Transport { get => _transport; set => throw new NotSupportedException(); }
            public override IFeatureCollection Features { get; } = new FeatureCollection();
            public override IDictionary<object, object?> Items { get; set; } = new Dictionary<object, object?>();

            private sealed class DuplexPipe : IDuplexPipe
            {
                public DuplexPipe(PipeReader input, PipeWriter output)
                {
                    Input = input;
                    Output = output;
                }

                public PipeReader Input { get; }
                public PipeWriter Output { get; }
            }

        }
    }
}
