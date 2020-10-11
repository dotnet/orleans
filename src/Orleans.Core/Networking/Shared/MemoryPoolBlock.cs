﻿using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace Orleans.Networking.Shared
{
    /// <summary>
    /// Block tracking object used by the byte buffer memory pool. A slab is a large allocation which is divided into smaller blocks. The
    /// individual blocks are then treated as independent array segments.
    /// </summary>
    internal sealed class MemoryPoolBlock : IMemoryOwner<byte>
    {
        private readonly int _offset;
        private readonly int _length;

        /// <summary>
        /// This object cannot be instantiated outside of the static Create method
        /// </summary>
        internal MemoryPoolBlock(SlabMemoryPool pool, MemoryPoolSlab slab, int offset, int length)
        {
            _offset = offset;
            _length = length;

            Pool = pool;
            Slab = slab;

            Memory = MemoryMarshal.CreateFromPinnedArray(slab.Array, _offset, _length);
        }

        /// <summary>
        /// Back-reference to the memory pool which this block was allocated from. It may only be returned to this pool.
        /// </summary>
        public SlabMemoryPool Pool { get; }

        /// <summary>
        /// Back-reference to the slab from which this block was taken, or null if it is one-time-use memory.
        /// </summary>
        public MemoryPoolSlab Slab { get; }

        public Memory<byte> Memory { get; }

        ~MemoryPoolBlock()
        {
            Pool.RefreshBlock(Slab, _offset, _length);
        }

        public void Dispose()
        {
            Pool.Return(this);
        }

        public void Lease()
        {
        }
    }
}
