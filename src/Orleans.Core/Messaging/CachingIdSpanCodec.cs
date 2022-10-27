using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Serialization.Buffers;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// A serializer for <see cref="IdSpan"/> which caches values and avoids re-encoding and unnecessary allocations.
    /// </summary>
    internal sealed class CachingIdSpanCodec
    {
        private static readonly LRU<IdSpan, IdSpan> SharedCache = new(maxSize: 128_000, maxAge: TimeSpan.FromHours(1));

        // Purge entries which have not been accessed in over 2 minutes. 
        private const long PurgeAfterMilliseconds = 2 * 60 * 1000;

        // Scan for entries which are expired every minute
        private const long GarbageCollectionIntervalMilliseconds = 60 * 1000;

        private readonly Dictionary<int, (byte[] Value, long LastSeen)> _cache = new();
        private long _lastGarbageCollectionTimestamp;

        public CachingIdSpanCodec()
        {
            _lastGarbageCollectionTimestamp = Environment.TickCount64;
        }

        public IdSpan ReadRaw<TInput>(ref Reader<TInput> reader)
        {
            var currentTimestamp = Environment.TickCount64;

            var length = reader.ReadVarUInt32();
            if (length == 0)
                return default;

            var hashCode = reader.ReadInt32();

            IdSpan result = default;
            byte[] payloadArray = default;
            if (!reader.TryReadBytes((int)length, out var payloadSpan))
            {
                payloadSpan = payloadArray = reader.ReadBytes(length);
            }

            ref var cacheEntry = ref CollectionsMarshal.GetValueRefOrAddDefault(_cache, hashCode, out var exists);
            if (exists && payloadSpan.SequenceEqual(cacheEntry.Value))
            {
                result = IdSpan.UnsafeCreate(cacheEntry.Value, hashCode);
            }
            else
            {
                result = IdSpan.UnsafeCreate(payloadArray ?? payloadSpan.ToArray(), hashCode);

                // Before adding this value to the private cache and returning it, intern it via the shared cache to hopefully reduce duplicates.
                result = SharedCache.GetOrAdd(result, static (_, key) => key, (object)null);

                // Update the cache. If there is a hash collision, the last entry wins.
                cacheEntry.Value = IdSpan.UnsafeGetArray(result);
            }
            cacheEntry.LastSeen = currentTimestamp;

            // Perform periodic maintenance to prevent unbounded memory leaks.
            if (currentTimestamp - _lastGarbageCollectionTimestamp > GarbageCollectionIntervalMilliseconds)
            {
                PurgeStaleEntries();
                _lastGarbageCollectionTimestamp = currentTimestamp;
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void PurgeStaleEntries()
        {
            var currentTimestamp = Environment.TickCount64;
            foreach (var entry in _cache)
            {
                if (currentTimestamp - entry.Value.LastSeen > PurgeAfterMilliseconds)
                {
                    _cache.Remove(entry.Key);
                }
            }
        }

        public void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer, IdSpan value) where TBufferWriter : IBufferWriter<byte>
        {
            IdSpanCodec.WriteRaw(ref writer, value);
            SharedCache.GetOrAdd(value, static (_, key) => key, (object)null);
        }
    }
}
