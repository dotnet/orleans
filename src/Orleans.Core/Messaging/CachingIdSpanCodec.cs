using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;
using System.Runtime.InteropServices;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// A serializer for <see cref="IdSpan"/> which caches values and avoids re-encoding and unnecessary allocations.
    /// </summary>
    internal sealed class CachingIdSpanCodec
    {
        internal static LRU<IdSpan, (IdSpan Value, byte[] Encoded)> SharedCache { get; } = new(maxSize: 128_000, maxAge: TimeSpan.FromHours(1));

        // Purge entries which have not been accessed in over 2 minutes. 
        private const long PurgeAfterMilliseconds = 2 * 60 * 1000;

        // Scan for entries which are expired every minute
        private const long GarbageCollectionIntervalMilliseconds = 60 * 1000;

        private readonly Dictionary<int, (byte[] Encoded, IdSpan Value, long LastSeen)> _cache = new();
        private long _lastGarbageCollectionTimestamp;

        public CachingIdSpanCodec()
        {
            _lastGarbageCollectionTimestamp = Environment.TickCount64;
        }

        public IdSpan ReadRaw<TInput>(ref Reader<TInput> reader)
        {
            var currentTimestamp = Environment.TickCount64;

            IdSpan result = default;
            byte[] payloadArray = default;
            var length = reader.ReadVarInt32();
            if (length == -1)
            {
                return default;
            }

            if (!reader.TryReadBytes(length, out var payloadSpan))
            {
                payloadSpan = payloadArray = reader.ReadBytes((uint)length);
            }

            var innerReader = Reader.Create(payloadSpan, null);
            var hashCode = innerReader.ReadInt32();

            ref var cacheEntry = ref CollectionsMarshal.GetValueRefOrAddDefault(_cache, hashCode, out var exists);
            if (exists && new ReadOnlySpan<byte>(cacheEntry.Encoded).SequenceEqual(payloadSpan))
            {
                result = cacheEntry.Value;
                cacheEntry.LastSeen = currentTimestamp;
            }

            if (!exists || result.IsDefault)
            {
                if (payloadArray is null)
                {
                    payloadArray = new byte[length];
                    payloadSpan.CopyTo(payloadArray);
                }

                result = ReadRawInner(ref innerReader, hashCode);

                // Before adding this value to the private cache and returning it, intern it via the shared cache to hopefully reduce duplicates.
                (result, _) = SharedCache.GetOrAdd(result, static (encoded, key) => (key, encoded), payloadArray);

                // Update the cache. If there is a hash collision, the last entry wins.
                cacheEntry.Encoded = payloadArray;
                cacheEntry.Value = result;
                cacheEntry.LastSeen = currentTimestamp;
            }

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
            var currentTimestamp = Environment.TickCount64;
            if (value.IsDefault)
            {
                writer.WriteVarInt32(-1);
                return;
            }

            var hashCode = value.GetHashCode();
            ref var cacheEntry = ref CollectionsMarshal.GetValueRefOrAddDefault(_cache, hashCode, out var exists);
            if (exists && value.Equals(cacheEntry.Value))
            {
                writer.WriteVarInt32(cacheEntry.Encoded.Length);
                writer.Write(cacheEntry.Encoded);

                cacheEntry.LastSeen = currentTimestamp;

                // Perform periodic maintenance to prevent unbounded memory leaks.
                if (currentTimestamp - _lastGarbageCollectionTimestamp > GarbageCollectionIntervalMilliseconds)
                {
                    PurgeStaleEntries();
                    _lastGarbageCollectionTimestamp = currentTimestamp;
                }

                return;
            }

            var innerWriter = Writer.Create(new PooledArrayBufferWriter(), null);
            innerWriter.WriteInt32(value.GetHashCode());
            WriteRawInner(ref innerWriter, value);
            innerWriter.Commit();

            writer.WriteVarInt32((int)innerWriter.Output.Length);
            innerWriter.Output.CopyTo(ref writer);
            var payloadArray = innerWriter.Output.ToArray();
            innerWriter.Dispose();

            // Before adding this value to the private cache, intern it via the shared cache to hopefully reduce duplicates.
            (_, payloadArray) = SharedCache.GetOrAdd(value, static (encoded, key) => (key, encoded), payloadArray);

            // If there is a hash collision, then the last seen entry will always win.
            cacheEntry.Encoded = payloadArray;
            cacheEntry.Value = value;
            cacheEntry.LastSeen = currentTimestamp;
        }

        /// <summary>
        /// Writes an <see cref="IdSpan"/> value to the provided writer without field framing.
        /// </summary>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value to write.</param>
        /// <typeparam name="TBufferWriter">The underlying buffer writer type.</typeparam>
        private static void WriteRawInner<TBufferWriter>(
            ref Writer<TBufferWriter> writer,
            IdSpan value)
            where TBufferWriter : IBufferWriter<byte>
        {
            var bytes = IdSpan.UnsafeGetArray(value);
            var bytesLength = value.IsDefault ? 0 : bytes.Length;
            writer.WriteVarUInt32((uint)bytesLength);
            writer.Write(bytes);
        }

        /// <summary>
        /// Reads an <see cref="IdSpan"/> value from a reader without any field framing.
        /// </summary>
        /// <typeparam name="TInput">The underlying reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="hashCode">The hash code for the span.</param>
        /// <returns>An <see cref="IdSpan"/>.</returns>
        private static IdSpan ReadRawInner<TInput>(ref Reader<TInput> reader, int hashCode)
        {
            var length = reader.ReadVarUInt32();
            var payloadArray = reader.ReadBytes(length);
            var value = IdSpan.UnsafeCreate(payloadArray, hashCode);
            return value;
        }
    }
}
