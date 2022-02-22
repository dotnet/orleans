using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;

namespace Orleans.Runtime.Messaging
{
    [GenerateSerializer]
    internal class ConnectionPreamble
    {
        [Id(0)]
        public NetworkProtocolVersion NetworkProtocolVersion { get; set; }

        [Id(1)]
        public GrainId NodeIdentity { get; set; }

        [Id(2)]
        public SiloAddress SiloAddress { get; set; }

        [Id(3)]
        public string ClusterId { get; set; }
    }

    internal class ConnectionPreambleHelper
    {
        private const int MaxPreambleLength = 1024;
        private readonly Serializer<ConnectionPreamble> _preambleSerializer;
        public ConnectionPreambleHelper(Serializer<ConnectionPreamble> preambleSerializer)
        {
            _preambleSerializer = preambleSerializer;
        }

        internal async ValueTask Write(ConnectionContext connection, ConnectionPreamble preamble)
        {
            var output = connection.Transport.Output;
            using var outputWriter = new PrefixingBufferWriter<byte, PipeWriter>(sizeof(int), 1024, MemoryPool<byte>.Shared);
            outputWriter.Reset(output);
            _preambleSerializer.Serialize(
                preamble,
                outputWriter);

            var length = outputWriter.CommittedBytes;

            if (length > MaxPreambleLength)
            {
                throw new InvalidOperationException($"Created preamble of length {length}, which is greater than maximum allowed size of {MaxPreambleLength}.");
            }

            WriteLength(outputWriter, length);

            var flushResult = await output.FlushAsync();
            if (flushResult.IsCanceled)
            {
                throw new OperationCanceledException("Flush canceled");
            }

            return;
        }

        private static void WriteLength(PrefixingBufferWriter<byte, PipeWriter> outputWriter, int length)
        {
            Span<byte> lengthSpan = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthSpan, length);
            outputWriter.Complete(lengthSpan);
        }

        internal async ValueTask<ConnectionPreamble> Read(ConnectionContext connection)
        {
            var input = connection.Transport.Input;

            var readResult = await input.ReadAsync();
            var buffer = readResult.Buffer;
            CheckForCompletion(ref readResult);
            while (buffer.Length < 4)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                readResult = await input.ReadAsync();
                buffer = readResult.Buffer;
                CheckForCompletion(ref readResult);
            }

            int ReadLength(ref ReadOnlySequence<byte> b)
            {
                Span<byte> lengthBytes = stackalloc byte[4];
                b.Slice(0, 4).CopyTo(lengthBytes);
                b = b.Slice(4);
                return BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            }

            var length = ReadLength(ref buffer);
            if (length > MaxPreambleLength)
            {
                throw new InvalidOperationException($"Remote connection sent preamble length of {length}, which is greater than maximum allowed size of {MaxPreambleLength}.");
            }

            while (buffer.Length < length)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                readResult = await input.ReadAsync();
                buffer = readResult.Buffer;
                CheckForCompletion(ref readResult);
            }

            var payloadBuffer = buffer.Slice(0, length);

            try
            {
                var preamble = _preambleSerializer.Deserialize(payloadBuffer);
                return preamble;
            }
            finally
            {
                input.AdvanceTo(payloadBuffer.End);
            }

            void CheckForCompletion(ref ReadResult r)
            {
                if (r.IsCanceled || r.IsCompleted) throw new InvalidOperationException("Connection terminated prematurely");
            }
        }
    }
}
