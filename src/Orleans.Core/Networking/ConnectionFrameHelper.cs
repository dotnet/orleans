using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// Provides framing utilities for connection middleware authors.
    /// <para>
    /// Wire format per frame: [4-byte LE length] [1-byte frame type] [payload].
    /// The length field equals 1 + payload.Length.
    /// </para>
    /// <para>
    /// Connection middlewares should follow the <see cref="ConnectionDelegate"/> pipeline pattern
    /// (like TLS) and register via <c>SiloConnectionOptions</c> or <c>ClientConnectionOptions</c>.
    /// Use these helpers for structured frame I/O within your middleware.
    /// </para>
    /// <para>
    /// This helper is optional. Middleware authors who need custom protocols can read/write
    /// directly from <c>context.Transport.Input</c> (PipeReader) and <c>context.Transport.Output</c>
    /// (PipeWriter) without using this class.
    /// </para>
    /// </summary>
    public static class ConnectionFrameHelper
    {
        /// <summary>
        /// Default maximum frame length (1 MB).
        /// </summary>
        public const int DefaultMaxFrameLength = 1024 * 1024;

        /// <summary>
        /// Prefix size: 4 bytes (LE length) + 1 byte (frame type) = 5 bytes.
        /// </summary>
        public const int FramePrefixSize = 5;

        /// <summary>
        /// Reads a single frame from the connection.
        /// </summary>
        public static async ValueTask<(byte FrameType, byte[] Payload)> ReadFrameAsync(
            ConnectionContext connection,
            CancellationToken cancellationToken,
            int maxFrameLength = DefaultMaxFrameLength)
        {
            var input = connection.Transport.Input;

            var readResult = await input.ReadAsync(cancellationToken);
            var buffer = readResult.Buffer;
            CheckCompletionWithData(ref readResult, 4);

            while (buffer.Length < 4)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                readResult = await input.ReadAsync(cancellationToken);
                buffer = readResult.Buffer;
                CheckCompletionWithData(ref readResult, 4);
            }

            var lengthBytes = new byte[4];
            buffer.Slice(0, 4).CopyTo(lengthBytes);
            var frameLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);

            if (frameLength < 1)
                throw new InvalidOperationException($"Invalid frame length: {frameLength}. Minimum is 1 (frame type byte).");
            if (frameLength > maxFrameLength)
                throw new InvalidOperationException($"Frame length {frameLength} exceeds maximum allowed length of {maxFrameLength}.");

            var totalNeeded = 4 + frameLength;
            while (buffer.Length < totalNeeded)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                readResult = await input.ReadAsync(cancellationToken);
                buffer = readResult.Buffer;
                CheckCompletionWithData(ref readResult, totalNeeded);
            }

            var frameSlice = buffer.Slice(4, frameLength);
            var frameBytes = new byte[frameLength];
            frameSlice.CopyTo(frameBytes);

            byte frameType = frameBytes[0];
            byte[] payload;
            if (frameLength > 1)
            {
                payload = new byte[frameLength - 1];
                Array.Copy(frameBytes, 1, payload, 0, payload.Length);
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            input.AdvanceTo(buffer.GetPosition(totalNeeded));

            return (frameType, payload);
        }

        /// <summary>
        /// Writes a frame with the given type and raw payload bytes.
        /// </summary>
        public static async ValueTask WriteFrameAsync(
            ConnectionContext connection,
            byte frameType,
            byte[] payload,
            CancellationToken cancellationToken)
        {
            WriteFrameToOutput(connection.Transport.Output, frameType, payload);

            var flushResult = await connection.Transport.Output.FlushAsync(cancellationToken);
            if (flushResult.IsCanceled)
                throw new OperationCanceledException("Flush canceled while writing frame.");
        }

        /// <summary>
        /// Writes a frame using zero-copy framing via <see cref="PrefixingBufferWriter"/>.
        /// The <paramref name="writePayload"/> delegate writes payload directly into the transport pipe buffer.
        /// </summary>
        public static async ValueTask WriteFrameAsync(
            ConnectionContext connection,
            byte frameType,
            Action<IBufferWriter<byte>> writePayload,
            CancellationToken cancellationToken)
        {
            WriteFrameWithPrefixingWriter(connection.Transport.Output, frameType, writePayload, MemoryPool<byte>.Shared);

            var flushResult = await connection.Transport.Output.FlushAsync(cancellationToken);
            if (flushResult.IsCanceled)
                throw new OperationCanceledException("Flush canceled while writing frame.");
        }

        /// <summary>
        /// Writes a length-prefixed UTF-8 string (4-byte LE length + bytes) into a buffer.
        /// </summary>
        public static void WriteLengthPrefixedString(IBufferWriter<byte> writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);

            var lengthSpan = writer.GetSpan(4);
            BinaryPrimitives.WriteInt32LittleEndian(lengthSpan, bytes.Length);
            writer.Advance(4);

            if (bytes.Length > 0)
            {
                var dataSpan = writer.GetSpan(bytes.Length);
                bytes.CopyTo(dataSpan);
                writer.Advance(bytes.Length);
            }
        }

        /// <summary>
        /// Reads a length-prefixed UTF-8 string from a payload byte array.
        /// </summary>
        public static string ReadLengthPrefixedString(byte[] data, ref int offset)
        {
            if (offset + 4 > data.Length)
                throw new InvalidOperationException("Not enough data to read string length.");

            var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
            offset += 4;

            if (length < 0)
                throw new InvalidOperationException($"Invalid string length: {length}.");
            if (length == 0)
                return string.Empty;
            if (offset + length > data.Length)
                throw new InvalidOperationException($"Not enough data to read string of length {length}. Available: {data.Length - offset}.");

            var result = Encoding.UTF8.GetString(data, offset, length);
            offset += length;
            return result;
        }

        private static void WriteFrameToOutput(PipeWriter output, byte frameType, byte[] payload)
        {
            var payloadLength = payload?.Length ?? 0;
            var frameLength = 1 + payloadLength;

            Span<byte> prefix = stackalloc byte[FramePrefixSize];
            BinaryPrimitives.WriteInt32LittleEndian(prefix, frameLength);
            prefix[4] = frameType;
            output.Write(prefix);

            if (payloadLength > 0)
            {
                output.Write(payload);
            }
        }

        private static void WriteFrameWithPrefixingWriter(
            PipeWriter output,
            byte frameType,
            Action<IBufferWriter<byte>> writeBody,
            MemoryPool<byte> memoryPool)
        {
            using var prefixWriter = new PrefixingBufferWriter(FramePrefixSize, 256, memoryPool);
            prefixWriter.Init(output);

            try
            {
                writeBody?.Invoke(prefixWriter);

                var payloadLength = prefixWriter.CommittedBytes;
                var frameLength = 1 + payloadLength;
                Span<byte> prefix = stackalloc byte[FramePrefixSize];
                BinaryPrimitives.WriteInt32LittleEndian(prefix, frameLength);
                prefix[4] = frameType;

                prefixWriter.Complete(prefix);
            }
            finally
            {
                prefixWriter.Reset();
            }
        }

        private static void CheckCompletionWithData(ref ReadResult result, long required)
        {
            if (result.IsCanceled)
                throw new InvalidOperationException("Connection terminated during frame exchange.");
            if (result.IsCompleted && result.Buffer.Length < required)
                throw new InvalidOperationException("Connection terminated during frame exchange.");
        }
    }
}
