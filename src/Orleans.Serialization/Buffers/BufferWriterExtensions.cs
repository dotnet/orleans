using Orleans.Serialization.Session;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Buffers
{
    /// <summary>
    /// Extensions for working with <see cref="IBufferWriter{Byte}"/> implementations.
    /// </summary>
    public static class BufferWriterExtensions
    {
        /// <summary>
        /// Creates a <see cref="Writer{TBufferWriter}"/> instance for the provided buffer.
        /// </summary>
        /// <typeparam name="TBufferWriter">The type of the buffer writer.</typeparam>
        /// <param name="buffer">The buffer.</param>
        /// <param name="session">The session.</param>
        /// <returns>A new writer.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Writer<TBufferWriter> CreateWriter<TBufferWriter>(this TBufferWriter buffer, SerializerSession session) where TBufferWriter : IBufferWriter<byte>
        {
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(session);
#else
            if (session is null) throw new ArgumentNullException(nameof(session));
#endif
            return Writer.Create(buffer, session);
        }
    }
}