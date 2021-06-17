using System;
using System.Buffers;

namespace Orleans.Runtime.Messaging
{
    internal interface IMessageSerializer
    {
        (int HeaderLength, int BodyLength) Write<TBufferWriter>(ref TBufferWriter writer, Message message) where TBufferWriter : IBufferWriter<byte>;
        
        /// <returns>
        /// The minimum number of bytes in <paramref name="input"/> before trying again, or 0 if a message was successfully read.
        /// </returns>
        (int RequiredBytes, int HeaderLength, int BodyLength) TryRead(ref ReadOnlySequence<byte> input, out Message message);
    }

    internal struct MessageBufferWriter<TBufferWriter> : IBufferWriter<byte> where TBufferWriter : IBufferWriter<byte>
    {
        private readonly PrefixingBufferWriter<byte, TBufferWriter> _buffer;
        public MessageBufferWriter(PrefixingBufferWriter<byte, TBufferWriter> buffer) => _buffer = buffer;
        public void Advance(int count) => _buffer.Advance(count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);
    }
}
