using StackExchange.Redis;

namespace Orleans.Journaling;

/// <summary>
/// The partition keys of one journal, which journal procedures take as their first arguments.
/// </summary>
internal sealed class GarnetJournalKeySet
{
    /// <summary>The number of procedure arguments the keys occupy.</summary>
    public const int Count = 6;

    public GarnetJournalKeySet(string mapId, int bucket, byte[] encodedJournalId)
    {
        Bucket = bucket;
        EncodedId = encodedJournalId;
        BucketKey = GarnetJournalKeys.GetBucketKey(mapId, bucket);
        MetadataKey = GarnetJournalKeys.GetMetadataKey(bucket, encodedJournalId);
        DataKey = GarnetJournalKeys.GetDataKey(bucket, encodedJournalId);
        IndexKey = GarnetJournalKeys.GetIndexKey(bucket);
        MarkerKey = GarnetJournalKeys.GetMarkerKey(bucket);
    }

    /// <summary>Creates the keys of a journal from its encoded id.</summary>
    public static GarnetJournalKeySet Create(string mapId, int bucketCount, byte[] encodedJournalId)
        => new(mapId, GarnetJournalKeys.GetBucket(encodedJournalId, bucketCount), encodedJournalId);

    public int Bucket { get; }

    public byte[] EncodedId { get; }

    public RedisKey BucketKey { get; }

    public RedisKey MetadataKey { get; }

    public RedisKey DataKey { get; }

    public RedisKey IndexKey { get; }

    public RedisKey MarkerKey { get; }

    /// <summary>Writes the keys and the encoded id to the first <see cref="Count"/> elements of <paramref name="args"/>.</summary>
    public void WriteTo(object[] args)
    {
        args[0] = BucketKey;
        args[1] = MetadataKey;
        args[2] = DataKey;
        args[3] = IndexKey;
        args[4] = MarkerKey;
        args[5] = (RedisValue)EncodedId;
    }

    /// <summary>Gets the arguments of a bucket procedure (bucket key, index key, marker key) for a bucket.</summary>
    public static object[] GetBucketArgs(string mapId, int bucket, params object[] rest)
    {
        var args = new object[3 + rest.Length];
        args[0] = (RedisKey)GarnetJournalKeys.GetBucketKey(mapId, bucket);
        args[1] = (RedisKey)GarnetJournalKeys.GetIndexKey(bucket);
        args[2] = (RedisKey)GarnetJournalKeys.GetMarkerKey(bucket);
        rest.CopyTo(args, 3);
        return args;
    }
}
