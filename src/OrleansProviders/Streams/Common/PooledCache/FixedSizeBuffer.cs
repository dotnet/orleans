
using System;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Manages a contiguous block of memory.
    /// Calls purge action with itself as the purge request when it's signaled to purge.
    /// </summary>
    public class FixedSizeBuffer : PooledResource<FixedSizeBuffer>
    {
        private readonly byte[] buffer;
        private int count;
        private readonly int blockSize;
        private Action<IDisposable> purgeAction;

        /// <summary>
        /// Unique identifier of this buffer
        /// </summary>
        public object Id => buffer;

        /// <summary>
        /// Manages access to a fixed size byte buffer.
        /// </summary>
        /// <param name="blockSize"></param>
        public FixedSizeBuffer(int blockSize)
        {
            if (blockSize < 0)
            {
                throw new ArgumentOutOfRangeException("blockSize", "blockSize must be positive value.");
            }
            count = 0;
            this.blockSize = blockSize;
            buffer = new byte[blockSize];
        }

        /// <summary>
        /// Sets the purge callback that will be called when this buffer is being purged.  It notifies
        ///   users of the buffer that the buffer is no longer valid.  This class is passed to the purge as a
        ///   disposable.  When all resources referencing this buffer are released, this buffer needs be disposed.
        /// </summary>
        /// <param name="purge"></param>
        public void SetPurgeAction(Action<IDisposable> purge)
        {
            if (purge == null)
            {
                throw new ArgumentNullException("purge");
            }
            if (purgeAction != null)
            {
                throw new InvalidOperationException("Purge action is already set.");
            }
            purgeAction = purge;
        }

        /// <summary>
        /// Try to get a segment with a buffer of the specified size from this block.
        /// Fail if there is not enough space available
        /// </summary>
        /// <param name="size"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public bool TryGetSegment(int size, out ArraySegment<byte> value)
        {
            value = default(ArraySegment<byte>);
            if (size > blockSize - count)
            {
                return false;
            }
            value = new ArraySegment<byte>(buffer, count, size);
            count += size;
            return true;
        }

        /// <summary>
        /// Reset state and calls purge with its buffer, indicating that any segments using that address are no longer valid.
        /// </summary>
        public override void SignalPurge()
        {
            count = 0;
            purgeAction?.Invoke(this);
            purgeAction = null;
        }
    }
}
