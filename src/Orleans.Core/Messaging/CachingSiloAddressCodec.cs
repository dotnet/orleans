using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;

namespace Orleans.Runtime.Messaging
{
    /// <summary>
    /// A serializer for <see cref="SiloAddress"/> which caches values and avoids re-encoding and unnecessary allocations.
    /// </summary>
    internal sealed class CachingSiloAddressCodec
    {
        internal static LRU<SiloAddress, (SiloAddress Value, byte[] Encoded)> SharedCache { get; } = new(maxSize: 128_000, maxAge: TimeSpan.FromHours(1));

        // Purge entries which have not been accessed in over 2 minutes.
        private const long PurgeAfterMilliseconds = 2 * 60 * 1000;

        // Scan for entries which are expired every minute
        private const long GarbageCollectionIntervalMilliseconds = 60 * 1000;

        private readonly Dictionary<int, (byte[] Encoded, SiloAddress Value, long LastSeen)> _cache = new();
        private long _lastGarbageCollectionTimestamp;

        public CachingSiloAddressCodec()
        {
            _lastGarbageCollectionTimestamp = Environment.TickCount64;
        }

        public SiloAddress ReadRaw<TInput>(ref Reader<TInput> reader)
        {
            var currentTimestamp = Environment.TickCount64;

            SiloAddress result = null;
            byte[] payloadArray = default;
            var length = reader.ReadVarInt32();
            if (length == -1)
            {
                return null;
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

            if (result is null)
            {
                if (payloadArray is null)
                {
                    payloadArray = new byte[length];
                    payloadSpan.CopyTo(payloadArray);
                }

                result = ReadSiloAddressInner(ref innerReader);
                result.InternalSetConsistentHashCode(hashCode);

                // Before adding this value to the private cache and returning it, intern it via the shared cache to hopefully reduce duplicates.
                (result, _) = SharedCache.GetOrAdd(result, static (encoded, key) => (key, encoded), payloadArray);

                // If there is a hash collision, then the last seen entry will always win.
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

        private static SiloAddress ReadSiloAddressInner<TInput>(ref Reader<TInput> reader)
        {
            IPAddress ip;
            var length = (int)reader.ReadVarUInt32();
            if (reader.TryReadBytes(length, out var bytes))
            {
                ip = new IPAddress(bytes);
            }
            else
            {
                var addressBytes = reader.ReadBytes((uint)length);
                ip = new IPAddress(addressBytes);
            }
            var port = (int)reader.ReadVarUInt32();
            var generation = reader.ReadInt32();

            return SiloAddress.New(ip, port, generation);
        }

        public void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer, SiloAddress value) where TBufferWriter : IBufferWriter<byte>
        {
            var currentTimestamp = Environment.TickCount64;
            if (value is null)
            {
                writer.WriteVarInt32(-1);
                return;
            }

            var hashCode = value.GetConsistentHashCode();
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
            innerWriter.WriteInt32(value.GetConsistentHashCode());
            WriteSiloAddressInner(ref innerWriter, value);
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

        private static void WriteSiloAddressInner<TBufferWriter>(ref Writer<TBufferWriter> writer, SiloAddress value) where TBufferWriter : IBufferWriter<byte>
        {
            var ep = value.Endpoint;
            Unsafe.SkipInit(out Guid tmp); // workaround for C#10 limitation around ref scoping (C#11 will add scoped ref parameters)
            var buffer = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref tmp, 1));
            if (!ep.Address.TryWriteBytes(buffer, out var length)) throw new NotSupportedException();

            // IP
            var bytes = buffer[..length];
            writer.WriteVarUInt32((uint)bytes.Length);
            writer.Write(bytes);

            // Port
            writer.WriteVarUInt32((uint)ep.Port);

            // Generation
            writer.WriteInt32(value.Generation);
        }
    }
}
