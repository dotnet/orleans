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
/// <see cref="HostId"/>, with their durable state under <see cref="DataDirectory"/> or in
/// <see cref="PartitionBlobContainer"/>, and connects to the others at the endpoints their hosts publish in the map.
/// </para>
/// <para>
/// The first silo to start writes <see cref="InitialRangeMap"/> to an empty MetaStore. If no initial map is configured,
/// a silo with a <see cref="DataDirectory"/> or a <see cref="PartitionBlobContainer"/> writes a map with one partition,
/// <c>p0</c>, which it hosts.
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

    /// <summary>The default time for which a partition retains a deleted journal's tombstone.</summary>
    public static readonly TimeSpan DEFAULT_TOMBSTONE_RETENTION = TimeSpan.FromMinutes(5);

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
    /// The range map assigns partitions to hosts by this identifier. If it is <see langword="null"/>, a silo with
    /// <see cref="PartitionBlobContainer"/> uses its silo address, which identifies one run of the silo, so its
    /// partitions fail over when it dies; a silo with a <see cref="DataDirectory"/> generates an identifier on first
    /// start and stores it in the directory, since its partitions stay in that directory; and other silos host no
    /// partitions. Set it explicitly to name hosts in <see cref="InitialRangeMap"/>.
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
    /// no partitions, and silos whose partitions are in <see cref="PartitionBlobContainer"/>, need no directory.
    /// </remarks>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// Gets or sets the Azure Storage container which holds every partition's durable state, shared by every silo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When set, each partition stores its append-only file, hybrid log, and checkpoints as page blobs under its
    /// <see cref="PartitionHosting.StorageLocation"/> in this container, so any silo can host it. A silo starts a
    /// partition by taking its storage over: it breaks the leases of the previous host, whose later writes then fail at
    /// the storage service, and recovers the partition from the blobs. An acknowledged write therefore survives the loss
    /// of the silo, and the machine, which acknowledged it.
    /// </para>
    /// <para>
    /// Unless <see cref="HostId"/> is set, silos are identified by their silo addresses, and every silo assigns each
    /// partition whose host Orleans membership has declared dead, or whose host is not a silo address, to an active silo.
    /// </para>
    /// </remarks>
    public Azure.Storage.Blobs.BlobContainerClient? PartitionBlobContainer { get; set; }

    /// <summary>
    /// Gets or sets how often a silo reads the range map to start and stop the partitions assigned to it and, with
    /// <see cref="PartitionBlobContainer"/>, to fail partitions over from dead silos.
    /// </summary>
    public TimeSpan HostingInterval { get; set; } = TimeSpan.FromSeconds(1);

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

    /// <summary>
    /// Gets or sets how long a partition retains the tombstone of a deleted journal once its bucket's migration is done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A tombstone holds the ETags of the journal's recent mutations, which let a retried mutation recognize that it
    /// committed. When a partition drops a tombstone, it advances the bucket's collection generation past the one the
    /// deletion recorded, and rejects writes to absent journals which observed the absence at an earlier generation,
    /// so a retry never applies twice, whatever any silo's <see cref="OperationTimeout"/> or clock (D25).
    /// </para>
    /// <para>
    /// Retries which arrive after their tombstone is dropped fail with <see cref="Storage.InconsistentStateException"/>
    /// instead of reporting their committed outcome. Retain tombstones for longer than the longest
    /// <see cref="OperationTimeout"/> in the cluster, plus an allowance for clock skew between silos, which only affects
    /// when tombstones are dropped.
    /// </para>
    /// </remarks>
    public TimeSpan TombstoneRetention { get; set; } = DEFAULT_TOMBSTONE_RETENTION;

    /// <summary>
    /// Gets or sets how often a silo checks for migration work in the partitions it hosts, when it has none in progress.
    /// </summary>
    public TimeSpan MigrationInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets how often a silo drops expired tombstones and absent markers from the partitions it hosts.
    /// </summary>
    public TimeSpan MarkerCollectionInterval { get; set; } = TimeSpan.FromMinutes(1);

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
