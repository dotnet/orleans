using System.Buffers;

namespace Orleans.Runtime.Messaging
{
    internal interface IMessageSerializer
    {
        void Write<TBufferWriter>(ref TBufferWriter writer, Message message) where TBufferWriter : IBufferWriter<byte>;
        
        /// <returns>
        /// The minimum number of bytes in <paramref name="input"/> before trying again, or 0 if a message was successfully read.
        /// </returns>
        int TryRead(ref ReadOnlySequence<byte> input, out Message message);
    }
}
