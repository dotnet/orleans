using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Orleans.Serialization;

namespace Orleans.Runtime.Messaging
{
    internal static class ConnectionPreamble
    {
        private const int MaxPreambleLength = 1024;

        internal static async ValueTask Write(ConnectionContext connection, GrainId nodeIdentity, NetworkProtocolVersion protocolVersion, SiloAddress siloAddress)
        {
            var output = connection.Transport.Output;
            var outputWriter = new PrefixingBufferWriter<byte, PipeWriter>(sizeof(int), 1024, MemoryPool<byte>.Shared);
            outputWriter.Reset(output);
            var writer = new BinaryTokenStreamWriter2<PrefixingBufferWriter<byte, PipeWriter>>(outputWriter);

            writer.Write(nodeIdentity);
            writer.Write((byte)protocolVersion);

            if (siloAddress is null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.Write((byte)SerializationTokenType.SiloAddress);
                writer.Write(siloAddress);
            }

            writer.Commit();

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

        internal static async ValueTask<(GrainId, NetworkProtocolVersion, SiloAddress)> Read(ConnectionContext connection)
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
                var reader = new BinaryTokenStreamReader2(payloadBuffer);
                var grainId = reader.ReadGrainId();

                if (reader.Position >= payloadBuffer.Length)
                {
                    return (grainId, NetworkProtocolVersion.Version1, default);
                }

                var protocolVersion = (NetworkProtocolVersion)reader.ReadByte();

                SiloAddress siloAddress;
                var token = reader.ReadToken();
                switch (token)
                {
                    case SerializationTokenType.Null:
                        siloAddress = null;
                        break;
                    case SerializationTokenType.SiloAddress:
                        siloAddress = reader.ReadSiloAddress();
                        break;
                    default:
                        throw new NotSupportedException("Unexpected token while reading connection preamble. Expected SiloAddress, encountered " + token);
                }

                return (grainId, protocolVersion, siloAddress);
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
