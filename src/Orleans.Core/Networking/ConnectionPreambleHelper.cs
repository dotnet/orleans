using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Tasks;
using Orleans.Networking.Shared;

namespace Orleans.Runtime.Messaging
{
    internal static class ConnectionPreamble
    {
        private const int MaxPreambleLength = 1024;

        internal static Task Write(ConnectionContext connection, GrainId grainId)
        {
            var output = connection.Transport.Output;
            var grainIdByteArray = grainId.ToByteArray();

            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, grainIdByteArray.Length);
            var length = bytes.Length + grainIdByteArray.Length;

            if (length > MaxPreambleLength)
            {
                throw new InvalidOperationException($"Created preamble of length {length}, which is greater than maximum allowed size of {MaxPreambleLength}.");
            }

            var buffer = output.GetSpan(length);
            bytes.CopyTo(buffer);
            new ReadOnlySpan<byte>(grainIdByteArray).CopyTo(buffer.Slice(sizeof(int)));
            output.Advance(length);
            var flushTask = output.FlushAsync();

            if (flushTask.IsCompletedSuccessfully) return Task.CompletedTask;
            return flushTask.AsTask();
        }
                
        internal static async Task<GrainId> Read(ConnectionContext connection)
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

            var grainIdBuffer = buffer.Slice(0, length);
            input.AdvanceTo(grainIdBuffer.End);
            var grainIdBytes = new byte[Math.Min(length, 1024)];
            grainIdBuffer.CopyTo(grainIdBytes);
            return GrainIdExtensions.FromByteArray(grainIdBytes);

            void CheckForCompletion(ref ReadResult r)
            {
                if (r.IsCanceled || r.IsCompleted) throw new InvalidOperationException("Connection terminated prematurely");
            }
        }
    }
}
