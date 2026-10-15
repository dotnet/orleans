using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Garnet.common;
using Garnet.server;
using Tsavorite.core;

namespace Orleans.Journaling;

/// <summary>
/// Counts the journal operations a partition serves per bucket, for the balancer.
/// </summary>
/// <remarks>
/// Counts are cumulative from the partition's start and live only in memory: they are advisory, and the balancer
/// derives rates from differences between its own samples. Recovery replays logged operations, so the host resets the
/// counts once recovery completes.
/// </remarks>
internal sealed class GarnetJournalLoad
{
    private readonly ConcurrentDictionary<int, StrongBox<long>> _operations = new();

    /// <summary>Records an operation on the bucket named by <paramref name="bucketKey"/> (<c>oj:b:{map}:{bucket}</c>).</summary>
    public void Record(ReadOnlySpan<byte> bucketKey)
    {
        var separator = bucketKey.LastIndexOf((byte)':');
        if (separator >= 0 && Utf8Parser.TryParse(bucketKey[(separator + 1)..], out int bucket, out var consumed) && consumed == bucketKey.Length - separator - 1)
        {
            Interlocked.Increment(ref _operations.GetOrAdd(bucket, static _ => new StrongBox<long>()).Value);
        }
    }

    /// <summary>Gets the number of operations recorded on <paramref name="bucket"/>.</summary>
    public long Get(int bucket) => _operations.TryGetValue(bucket, out var count) ? Volatile.Read(ref count.Value) : 0;

    /// <summary>Discards every count.</summary>
    public void Reset() => _operations.Clear();
}

/// <summary>
/// Reports load for ranges of buckets. Arguments: pairs of first and last buckets. Returns <c>[1, operations, journals,
/// ...]</c>: for each bucket in order, the operations served since the partition started, and the size of its index.
/// It reports at most 2^20 buckets per call, and stops at the first malformed pair or the first range which would exceed that.
/// </summary>
/// <remarks>
/// A non-transactional procedure: it locks nothing and writes nothing, so Garnet does not log it, and its counts are
/// approximate.
/// </remarks>
internal sealed unsafe class GarnetJournalLoadProcedure(GarnetJournalLoad load) : CustomProcedure
{
    private const int MaxBuckets = 1 << 20;

    public override bool Execute<TGarnetApi>(TGarnetApi garnetApi, ref CustomProcedureInput procInput, ref MemoryResult<byte> output)
    {
        var response = new GarnetRespArrayWriter();
        response.WriteBulk(GarnetJournalProtocol.SuccessStatus);
        var offset = 0;
        var remaining = (long)MaxBuckets;
        while (true)
        {
            var firstArg = GetNextArg(ref procInput, ref offset);
            var lastArg = GetNextArg(ref procInput, ref offset);
            if (!firstArg.IsValid || !lastArg.IsValid
                || !Utf8Parser.TryParse(firstArg.ReadOnlySpan, out int first, out _)
                || !Utf8Parser.TryParse(lastArg.ReadOnlySpan, out int last, out _)
                || first < 0 || last < first)
            {
                break;
            }

            // Lengths are computed in 64 bits and the loop runs over an offset, so no range can overflow or exceed the budget.
            var length = (long)last - first + 1;
            if (length > remaining)
            {
                break;
            }

            remaining -= length;
            for (var i = 0; i < (int)length; i++)
            {
                var bucket = first + i;
                response.WriteBulk(load.Get(bucket));
                var indexKey = GarnetJournalKeys.GetIndexKey(bucket);
                fixed (byte* key = indexKey)
                {
                    response.WriteBulk(garnetApi.SetLength(PinnedSpanByte.FromPinnedPointer(key, indexKey.Length), out var count) == GarnetStatus.OK ? count : 0);
                }
            }
        }

        response.CopyTo(ref output);
        return true;
    }
}
