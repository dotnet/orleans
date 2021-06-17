using Orleans.Serialization.Session;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Buffers
{
    public static class BufferWriterExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Writer<TBufferWriter> CreateWriter<TBufferWriter>(this TBufferWriter buffer, SerializerSession session) where TBufferWriter : IBufferWriter<byte>
        {
            if (session is null)
            {
                ThrowSessionNull();
            }

            return Writer.Create(buffer, session);

            [MethodImpl(MethodImplOptions.NoInlining)]
            void ThrowSessionNull() => throw new ArgumentNullException(nameof(session));
        }
    }
}