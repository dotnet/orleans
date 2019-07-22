using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Orleans.Networking.Shared;
using Orleans.Serialization;

namespace Orleans.Runtime.Messaging
{
    internal static class ConnectionPreamble
    {
        private const int MaxPreambleLength = 1024;

        internal static Task Write(ConnectionContext connection, GrainId grainId, SiloAddress siloAddress)
        {
            var output = connection.Transport.Output;
            var outputWriter = new PrefixingBufferWriter<byte, PipeWriter>(output, sizeof(int), 1024, MemoryPool<byte>.Shared);
            var writer = new BinaryTokenStreamWriter2<PrefixingBufferWriter<byte, PipeWriter>>(outputWriter);

            writer.Write(grainId);
            if (!(siloAddress is null)) writer.Write(siloAddress);
            writer.Commit();

            var length = outputWriter.CommittedBytes;

            if (length > MaxPreambleLength)
            {
                throw new InvalidOperationException($"Created preamble of length {length}, which is greater than maximum allowed size of {MaxPreambleLength}.");
            }

            Span<byte> lengthSpan = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthSpan, length);
            outputWriter.Complete(lengthSpan);
            
            var flushTask = output.FlushAsync();

            if (flushTask.IsCompletedSuccessfully) return Task.CompletedTask;
            return flushTask.AsTask();
        }
                
        internal static async Task<(GrainId, SiloAddress)> Read(ConnectionContext connection)
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
            input.AdvanceTo(payloadBuffer.End);

            var reader = new BinaryTokenStreamReader2(payloadBuffer);
            var grainId = reader.ReadGrainId();
            SiloAddress siloAddress = null;
            if (reader.Position < payloadBuffer.Length)
            {
                siloAddress = reader.ReadSiloAddress();
            }

            return (grainId, siloAddress);

            void CheckForCompletion(ref ReadResult r)
            {
                if (r.IsCanceled || r.IsCompleted) throw new InvalidOperationException("Connection terminated prematurely");
            }
        }
    }
}
