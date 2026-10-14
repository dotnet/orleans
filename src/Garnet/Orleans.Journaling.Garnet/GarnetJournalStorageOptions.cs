using Orleans.Journaling.Ranges;

namespace Orleans.Journaling;

/// <summary>
/// Options for journal storage on embedded Garnet partitions.
/// </summary>
/// <remarks>
/// <para>
/// Each journal maps to one of the range map's buckets by a hash of its id, and each bucket is owned by one partition:
/// one Garnet instance with its own append-only file and checkpoints. The range map, held in <see cref="MetaStore"/>,
/// assigns ranges of buckets to partitions and partitions to hosts. A silo hosts the partitions assigned to
/// <see cref="HostId"/>, with their durable state under <see cref="DataDirectory"/>, and connects to the others at the
/// endpoints their hosts publish in the map.
/// </para>
/// <para>
/// The first silo to start writes <see cref="InitialRangeMap"/> to an empty MetaStore. If no initial map is configured,
/// a silo with a <see cref="DataDirectory"/> writes a map with one partition, <c>p0</c>, which it hosts.
/// </para>
/// </remarks>
public sealed class GarnetJournalStorageOptions
{
    /// <summary>The default compaction threshold, in bytes.</summary>
    public const long DEFAULT_COMPACTION_THRESHOLD_BYTES = 8L * 1024 * 1024;

    /// <summary>The default number of buckets.</summary>
    public const int DEFAULT_BUCKET_COUNT = 4096;

    /// <summary>The default in-memory size of an embedded partition's hybrid log.</summary>
    public const string DEFAULT_LOG_MEMORY_SIZE = "1g";

    /// <summary>The default size of an embedded partition's hash index.</summary>
    public const string DEFAULT_INDEX_MEMORY_SIZE = "64m";

    /// <summary>The default AOF size at which an embedded partition takes a checkpoint.</summary>
    public const string DEFAULT_CHECKPOINT_AOF_SIZE = "256m";

    /// <summary>
    /// Gets or sets the store holding the range map. Every silo of the cluster must use the same store.
    /// </summary>
    /// <remarks>
    /// Use <see cref="AzureBlobRangeMetaStore"/> for durable clusters, or one shared <see cref="InMemoryRangeMetaStore"/>
    /// for tests and single-process development.
    /// </remarks>
    public IRangeMetaStore? MetaStore { get; set; }

    /// <summary>
    /// Gets or sets the identifier of this silo in <see cref="PartitionHosting.Host"/>.
    /// </summary>
    /// <remarks>
    /// The range map assigns partitions to hosts by this identifier, so it must be stable across restarts. If it is
    /// <see langword="null"/>, a silo with a <see cref="DataDirectory"/> generates an identifier on first start and
    /// stores it in the directory, and a silo without one hosts no partitions. Set it explicitly to name hosts in
    /// <see cref="InitialRangeMap"/>.
    /// </remarks>
    public string? HostId { get; set; }

    /// <summary>
    /// Gets or sets the range map written to an empty <see cref="MetaStore"/>.
    /// </summary>
    /// <remarks>
    /// Once the MetaStore holds a map, this property is ignored. If it is <see langword="null"/>, a silo with a
    /// <see cref="DataDirectory"/> writes a map in which one partition, <c>p0</c>, owns all <see cref="BucketCount"/>
    /// buckets and is hosted by this silo; other silos wait for a map.
    /// </remarks>
    public RangeMap? InitialRangeMap { get; set; }

    /// <summary>
    /// Gets or sets the directory under which this silo stores the partitions it hosts.
    /// </summary>
    /// <remarks>
    /// Each hosted partition's append-only file and checkpoints live in the subdirectory named by its
    /// <see cref="PartitionHosting.StorageLocation"/>. The directory must persist across silo restarts. Silos which host
    /// no partitions need no directory.
    /// </remarks>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// Gets or sets the endpoint on which this silo's partitions accept connections.
    /// </summary>
    /// <remarks>
    /// Defaults to free ports on the loopback interface, which suits single-machine clusters; multi-machine clusters
    /// require an address reachable from every silo. With port 0, each partition listens on a free port; otherwise the
    /// partitions this silo hosts listen on consecutive ports from this one, in partition id order. Hosts publish the
    /// address, or the machine's host name for a wildcard address, in the range map.
    /// </remarks>
    public System.Net.IPEndPoint? ListenEndPoint { get; set; }

    /// <summary>
    /// Gets or sets the number of buckets in the default initial range map. Must be a power of two.
    /// </summary>
    /// <remarks>The bucket count is fixed when the range map is created.</remarks>
    public int BucketCount { get; set; } = DEFAULT_BUCKET_COUNT;

    /// <summary>
    /// Gets or sets the journal length, in bytes, at which <see cref="IJournalStorage.IsCompactionRequested"/> returns <see langword="true"/>.
    /// </summary>
    public long CompactionThresholdBytes { get; set; } = DEFAULT_COMPACTION_THRESHOLD_BYTES;

    /// <summary>
    /// Gets or sets how long an operation retries while its partition is unreachable, its outcome is unknown, or its
    /// bucket is moving between partitions.
    /// </summary>
    /// <remarks>
    /// Retries reuse the operation's ETags, so a retry of an operation which already committed reports its success
    /// instead of applying it again.
    /// </remarks>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets the in-memory size of each hosted partition's hybrid log, for example <c>1g</c>.</summary>
    public string LogMemorySize { get; set; } = DEFAULT_LOG_MEMORY_SIZE;

    /// <summary>Gets or sets the size of each hosted partition's hash index, for example <c>64m</c>.</summary>
    public string IndexMemorySize { get; set; } = DEFAULT_INDEX_MEMORY_SIZE;

    /// <summary>
    /// Gets or sets the size of a hosted partition's append-only file at which it takes a checkpoint, for example <c>256m</c>.
    /// </summary>
    /// <remarks>
    /// A checkpoint compacts the hybrid log and truncates the append-only file, bounding disk use and recovery time.
    /// </remarks>
    public string CheckpointAofSize { get; set; } = DEFAULT_CHECKPOINT_AOF_SIZE;

    /// <summary>
    /// Gets or sets the stage of the silo lifecycle when storage is initialized.
    /// </summary>
    public int InitStage { get; set; } = ServiceLifecycleStage.RuntimeInitialize;
}
