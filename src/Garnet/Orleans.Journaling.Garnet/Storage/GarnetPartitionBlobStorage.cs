using System.Collections.Concurrent;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;
using Orleans.Journaling.Ranges;

namespace Orleans.Journaling;

/// <summary>
/// One hosting tenure's exclusive hold on a partition's durable storage: the page blobs under the partition's storage
/// location in an Azure Storage container, which hold every device of its embedded Garnet server (D12).
/// </summary>
/// <remarks>
/// <para>
/// A tenure holds a lease, with an id of its own, on the partition's manifest blob and on every page blob it writes.
/// Every write, manifest update, and deletion is conditioned on that lease, so the storage service rejects it once
/// another tenure has broken the lease. The manifest is the only record of which blobs hold the partition's state: a
/// tenure creates a blob, acquires its lease, and then registers it in the manifest, by a lease-conditioned update,
/// before writing to it; it unregisters a blob before deleting it. Recovery reads only registered blobs, and only a
/// takeover breaks leases.
/// </para>
/// <para>
/// <see cref="OpenAsync"/> takes the storage over: it breaks and acquires the manifest's lease, which fences every
/// manifest update of earlier tenures, records the tenure's hosting epoch in the manifest, breaks and acquires the lease
/// of every blob under the location, and then confirms, by another lease-conditioned update, that it still holds the
/// manifest. An earlier tenure wrote only to blobs it registered while it held the manifest, which therefore existed,
/// under its lease, before the takeover, and which the takeover fences. A concurrent takeover which breaks leases after
/// this one fails its confirmation, so it never recovers or serves. Afterwards, every write an earlier tenure completed
/// is in a registered blob and visible to recovery, and every later write of an earlier tenure fails, so it can never
/// acknowledge an operation that recovery does not see.
/// </para>
/// <para>
/// A tenure whose hosting epoch is lower than the manifest's does not take the storage over. Any failed write marks the
/// storage <see cref="Failed"/>; the Garnet server then acknowledges no further operation, and its host stops serving
/// the partition.
/// </para>
/// </remarks>
internal sealed class GarnetPartitionBlobStorage
{
    internal const string ManifestBlobName = "manifest.json";
    private const int ManifestFormat = 1;
    private const int MaxConcurrency = 16;

    private readonly BlobContainerClient _container;
    private readonly string _prefix;
    private readonly BlockBlobClient _manifestBlob;
    private readonly BlobLeaseClient _manifestLease;
    private readonly SemaphoreSlim _manifestWrites = new(1, 1);
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, Lazy<Task>> _creating = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _failed = new();
    private readonly ILogger? _logger;
    private Dictionary<string, BlobEntry> _blobs = new(StringComparer.Ordinal);
    private long _sequence;
    private Exception? _failure;
    private long _pageWrites;
    private long _bytesWritten;

    private GarnetPartitionBlobStorage(BlobContainerClient container, string prefix, long epoch, ILogger? logger)
    {
        _container = container;
        _prefix = prefix;
        Epoch = epoch;
        LeaseId = Guid.NewGuid().ToString();
        _logger = logger;
        _manifestBlob = container.GetBlockBlobClient(prefix + ManifestBlobName);
        _manifestLease = _manifestBlob.GetBlobLeaseClient(LeaseId);
    }

    /// <summary>Gets the hosting epoch of this tenure.</summary>
    public long Epoch { get; }

    /// <summary>Gets the id of the lease this tenure holds on the manifest and every blob it lists.</summary>
    public string LeaseId { get; }

    /// <summary>Gets a token which is cancelled once a write fails, including because a later tenure fenced this one.</summary>
    public CancellationToken Failed => _failed.Token;

    /// <summary>Gets the first write failure, if any.</summary>
    public Exception? Failure => Volatile.Read(ref _failure);

    /// <summary>Gets the conditions which every write of this tenure carries.</summary>
    public PageBlobRequestConditions WriteConditions => new() { LeaseId = LeaseId };

    /// <summary>Gets the number of page uploads this tenure's devices have completed.</summary>
    public long PageWrites => Interlocked.Read(ref _pageWrites);

    /// <summary>Gets the number of bytes this tenure's devices have uploaded.</summary>
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>Records a completed page upload.</summary>
    internal void OnPagesWritten(long bytes)
    {
        Interlocked.Increment(ref _pageWrites);
        Interlocked.Add(ref _bytesWritten, bytes);
    }

    /// <summary>
    /// Takes over the storage under <paramref name="storageLocation"/> in <paramref name="container"/> for a tenure at
    /// <paramref name="epoch"/>, fencing every earlier tenure.
    /// </summary>
    /// <exception cref="GarnetPartitionFencedException">A tenure with a later epoch holds the storage.</exception>
    public static async Task<GarnetPartitionBlobStorage> OpenAsync(
        BlobContainerClient container,
        string storageLocation,
        long epoch,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocation);
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        var storage = new GarnetPartitionBlobStorage(container, NormalizePath(storageLocation) + "/", epoch, logger);
        await storage.TakeOverAsync(cancellationToken).ConfigureAwait(false);
        return storage;
    }

    /// <summary>Normalizes a path to forward slashes, without leading or trailing separators.</summary>
    internal static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');

    /// <summary>
    /// Validates the storage locations of partitions on shared storage: each must be a relative blob path of non-empty
    /// segments other than <c>.</c> and <c>..</c>, and no two may be equal or nested, since a takeover fences every blob
    /// under its partition's location.
    /// </summary>
    /// <exception cref="InvalidOperationException">A location is invalid, or overlaps another.</exception>
    internal static void ValidateStorageLocations(IEnumerable<KeyValuePair<PartitionId, PartitionHosting>> partitions)
    {
        var locations = new List<(PartitionId Partition, string Location)>();
        foreach (var (partition, hosting) in partitions)
        {
            var location = NormalizePath(hosting.StorageLocation ?? string.Empty);
            if (location.Length == 0 || location.Split('/').Any(segment => segment is "" or "." or ".."))
            {
                throw new InvalidOperationException(
                    $"Garnet journal partition '{partition}' has storage location '{hosting.StorageLocation}', which is not a relative blob path of non-empty segments other than '.' and '..'.");
            }

            if (location.Split('/')[0] == GarnetJournalHostRegistry.ReservedSegment)
            {
                throw new InvalidOperationException(
                    $"Garnet journal partition '{partition}' has storage location '{hosting.StorageLocation}', under '{GarnetJournalHostRegistry.ReservedSegment}', which holds the registry of hosts.");
            }

            locations.Add((partition, location));
        }

        // Sorted ordinally, a location which equals or contains another sorts immediately before it or before a
        // location which it also contains, so checking neighbors finds every overlap.
        locations.Sort((x, y) => string.CompareOrdinal(x.Location + "/", y.Location + "/"));
        for (var i = 1; i < locations.Count; i++)
        {
            var (previous, previousLocation) = locations[i - 1];
            var (current, currentLocation) = locations[i];
            if ((currentLocation + "/").StartsWith(previousLocation + "/", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Garnet journal partitions '{previous}' and '{current}' have overlapping storage locations '{previousLocation}' and '{currentLocation}'. Partitions on shared storage need disjoint locations, because a takeover fences every blob under its location.");
            }
        }
    }

    /// <summary>Gets the client of a blob, named relative to the storage location.</summary>
    public PageBlobClient GetPageBlob(string name) => _container.GetPageBlobClient(_prefix + name);

    /// <summary>Gets the size of a blob, or <see langword="null"/> if the manifest does not list it.</summary>
    public long? GetSize(string name)
    {
        lock (_lock)
        {
            return _blobs.TryGetValue(name, out var entry) ? entry.Size : null;
        }
    }

    /// <summary>Lists the blobs whose names start with <paramref name="prefix"/>, with their creation sequence numbers.</summary>
    public List<(string Name, long Sequence)> List(string prefix)
    {
        lock (_lock)
        {
            return [.. _blobs.Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(entry => (entry.Key, entry.Value.Sequence))];
        }
    }

    /// <summary>
    /// Ensures that a blob exists under this tenure's lease and is registered in the manifest.
    /// </summary>
    public Task EnsureCreatedAsync(string name, long size)
    {
        if (!_creating.TryGetValue(name, out var creation))
        {
            if (GetSize(name) is not null)
            {
                return Task.CompletedTask;
            }

            creation = _creating.GetOrAdd(name, key => new Lazy<Task>(() => CreateAsync(key, size)));
        }

        return creation.Value;
    }

    /// <summary>Removes blobs from the manifest, then deletes them.</summary>
    public async Task DeleteAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        if (names.Count == 0)
        {
            return;
        }

        await UpdateManifestAsync(blobs =>
        {
            foreach (var name in names)
            {
                blobs.Remove(name);
            }
        }, cancellationToken).ConfigureAwait(false);

        await Parallel.ForEachAsync(
            names,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = cancellationToken },
            async (name, token) =>
            {
                try
                {
                    await GetPageBlob(name).DeleteAsync(conditions: new BlobRequestConditions { LeaseId = LeaseId }, cancellationToken: token).ConfigureAwait(false);
                }
                catch (RequestFailedException exception) when (exception.Status == 404)
                {
                }
                catch (RequestFailedException exception)
                {
                    throw Fail(exception);
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a failed write. A write rejected for its lease means that a later tenure fenced this one.
    /// </summary>
    /// <returns>The exception to report.</returns>
    public Exception Fail(Exception exception)
    {
        var failure = exception is GarnetPartitionFencedException || !IsLeaseFailure(exception)
            ? exception
            : new GarnetPartitionFencedException($"A later tenure fenced the storage of this Garnet journal partition (epoch {Epoch}).", exception);
        if (Interlocked.CompareExchange(ref _failure, failure, null) is null)
        {
            _logger?.LogWarning(failure, "Garnet journal partition storage at {Prefix} (epoch {Epoch}) failed a write.", _prefix, Epoch);
            _failed.Cancel();
        }

        return failure;
    }

    /// <summary>Releases the manifest's lease, so a later tenure need not break it.</summary>
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _manifestLease.ReleaseAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException)
        {
        }
    }

    /// <summary>Gets a value indicating whether the storage service rejected a request for its lease.</summary>
    internal static bool IsLeaseFailure(Exception exception)
        => exception is RequestFailedException { Status: 409 or 412, ErrorCode: { } code } && code.StartsWith("Lease", StringComparison.Ordinal);

    private async Task TakeOverAsync(CancellationToken cancellationToken)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await _manifestBlob.UploadAsync(
                new MemoryStream(Serialize(epoch: -1, sequence: 0, [])),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } },
                cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412)
        {
            // An earlier tenure created the manifest.
        }

        // Check the epoch before breaking the lease, so a stale host does not interrupt a later tenure.
        ThrowIfSuperseded(await ReadManifestAsync(cancellationToken).ConfigureAwait(false));
        await AcquireAsync(_manifestBlob, _manifestLease, cancellationToken).ConfigureAwait(false);
        Manifest manifest;
        try
        {
            manifest = await ReadManifestAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfSuperseded(manifest);
        }
        catch
        {
            await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // No earlier tenure can update the manifest from here on, so none can register another blob.
        _blobs = manifest.Blobs;
        _sequence = manifest.Sequence;
        await UpdateManifestAsync(static _ => { }, cancellationToken).ConfigureAwait(false);

        // Fence every blob under the location. Earlier tenures wrote only to blobs they registered, so fencing the
        // registered blobs suffices for safety; fencing the others too, such as a blob whose creation an earlier tenure
        // did not complete, lets this tenure replace them when it creates blobs of the same names. Storage locations are
        // disjoint (ValidateStorageLocations), so the listing holds only this partition's blobs.
        var targets = new HashSet<string>(manifest.Blobs.Keys, StringComparer.Ordinal);
        await foreach (var item in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, _prefix, cancellationToken).ConfigureAwait(false))
        {
            var name = item.Name[_prefix.Length..];
            if (name != ManifestBlobName)
            {
                targets.Add(name);
            }
        }

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = cancellationToken },
            async (name, token) =>
            {
                var blob = GetPageBlob(name);
                try
                {
                    await AcquireAsync(blob, blob.GetBlobLeaseClient(LeaseId), token).ConfigureAwait(false);
                }
                catch (RequestFailedException exception) when (exception.Status == 404)
                {
                    // A tenure registers a blob only after creating it, and deletes it only after unregistering it.
                    if (manifest.Blobs.ContainsKey(name))
                    {
                        throw new InvalidDataException($"The Garnet journal partition manifest at {_prefix} lists blob '{name}', which does not exist.", exception);
                    }
                }
            }).ConfigureAwait(false);

        // Confirm that this tenure still holds the manifest. A concurrent, earlier takeover may have broken the leases
        // of blobs which a later takeover fenced; it fails here, while any later takeover acquires the manifest after
        // this confirmation, and so breaks every lease after this tenure broke them.
        await UpdateManifestAsync(static _ => { }, cancellationToken).ConfigureAwait(false);

        _logger?.LogInformation(
            "Took over Garnet journal partition storage at {Prefix} at epoch {Epoch} from epoch {PreviousEpoch}, with {Count} blobs.",
            _prefix,
            Epoch,
            manifest.Epoch,
            manifest.Blobs.Count);
    }
    private void ThrowIfSuperseded(Manifest manifest)
    {
        if (manifest.Epoch > Epoch)
        {
            throw new GarnetPartitionFencedException(
                $"The storage of this Garnet journal partition is held at epoch {manifest.Epoch}, later than this tenure's epoch {Epoch}.");
        }
    }

    /// <summary>Breaks any lease on a blob and acquires the blob with this tenure's lease id.</summary>
    private static async Task AcquireAsync(BlobBaseClient blob, BlobLeaseClient lease, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await blob.GetBlobLeaseClient().BreakAsync(TimeSpan.Zero, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (exception.Status == 409 && exception.ErrorCode == BlobErrorCode.LeaseNotPresentWithLeaseOperation)
            {
            }

            try
            {
                await lease.AcquireAsync(BlobLeaseClient.InfiniteLeaseDuration, cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException exception) when (exception.Status == 409 && exception.ErrorCode == BlobErrorCode.LeaseAlreadyPresent && attempt < 10)
            {
                // Another tenure acquired the lease between the break and this acquisition; break it again.
                await Task.Delay(TimeSpan.FromMilliseconds(10 << Math.Min(attempt, 5)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Creates a blob, acquires it with this tenure's lease, and only then registers it in the manifest, so a blob is
    /// registered, and written to, only if this tenure acquired it before any later tenure took the storage over.
    /// </summary>
    private async Task CreateAsync(string name, long size)
    {
        try
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            // A caller which checked before an earlier creation of this blob registered it starts another creation;
            // never replace a registered blob, which may hold data.
            if (GetSize(name) is not null)
            {
                return;
            }

            var blob = GetPageBlob(name);
            try
            {
                await blob.CreateAsync(size, new PageBlobCreateOptions { Conditions = new PageBlobRequestConditions { IfNoneMatch = ETag.All } }).ConfigureAwait(false);

                // Acquiring fails if a later tenure has fenced the new blob since its creation.
                await blob.GetBlobLeaseClient(LeaseId).AcquireAsync(BlobLeaseClient.InfiniteLeaseDuration).ConfigureAwait(false);
            }
            catch (RequestFailedException exception) when (exception.Status == 409 && exception.ErrorCode == BlobErrorCode.BlobAlreadyExists)
            {
                // A blob which this tenure fenced when it took over: replace it with an empty blob. Only a takeover
                // breaks leases, so this fails if another tenure holds the blob.
                await blob.CreateAsync(size, new PageBlobCreateOptions { Conditions = WriteConditions }).ConfigureAwait(false);
            }

            await UpdateManifestAsync(blobs => blobs[name] = new(Interlocked.Increment(ref _sequence), size), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw Fail(exception);
        }
        finally
        {
            _creating.TryRemove(name, out _);
        }
    }
    /// <summary>Applies <paramref name="update"/> to the manifest and writes it under this tenure's lease.</summary>
    private async Task UpdateManifestAsync(Action<Dictionary<string, BlobEntry>> update, CancellationToken cancellationToken)
    {
        await _manifestWrites.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, BlobEntry> next;
            lock (_lock)
            {
                next = new(_blobs, StringComparer.Ordinal);
            }

            update(next);
            await _manifestBlob.UploadAsync(
                new MemoryStream(Serialize(Epoch, Interlocked.Read(ref _sequence), next)),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { LeaseId = LeaseId } },
                cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _blobs = next;
            }
        }
        catch (RequestFailedException exception)
        {
            throw Fail(exception);
        }
        finally
        {
            _manifestWrites.Release();
        }
    }

    private async Task<Manifest> ReadManifestAsync(CancellationToken cancellationToken)
    {
        var content = (await _manifestBlob.DownloadContentAsync(cancellationToken).ConfigureAwait(false)).Value.Content;
        try
        {
            using var document = JsonDocument.Parse(content.ToMemory());
            var root = document.RootElement;
            if (root.GetProperty("format").GetInt32() != ManifestFormat)
            {
                throw new InvalidDataException($"The Garnet journal partition manifest at {_prefix} has an unsupported format.");
            }

            var blobs = new Dictionary<string, BlobEntry>(StringComparer.Ordinal);
            foreach (var blob in root.GetProperty("blobs").EnumerateObject())
            {
                blobs[blob.Name] = new(blob.Value.GetProperty("seq").GetInt64(), blob.Value.GetProperty("size").GetInt64());
            }

            return new(root.GetProperty("epoch").GetInt64(), root.GetProperty("seq").GetInt64(), blobs);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"The Garnet journal partition manifest at {_prefix} is invalid.", exception);
        }
    }

    private static byte[] Serialize(long epoch, long sequence, Dictionary<string, BlobEntry> blobs)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", ManifestFormat);
            writer.WriteNumber("epoch", epoch);
            writer.WriteNumber("seq", sequence);
            writer.WriteStartObject("blobs");
            foreach (var (name, entry) in blobs.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(name);
                writer.WriteNumber("seq", entry.Sequence);
                writer.WriteNumber("size", entry.Size);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private readonly record struct BlobEntry(long Sequence, long Size);

    private sealed record Manifest(long Epoch, long Sequence, Dictionary<string, BlobEntry> Blobs);
}

/// <summary>
/// Thrown when a later hosting tenure of a Garnet journal partition has fenced this one, or holds the partition's storage.
/// </summary>
internal sealed class GarnetPartitionFencedException(string message, Exception? innerException = null) : IOException(message, innerException);
