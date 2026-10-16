using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Orleans.Journaling;

/// <summary>
/// The hosts eligible to host partitions on shared storage, as the failover agent sees them.
/// </summary>
internal interface IGarnetJournalHostRegistry
{
    /// <summary>Lists every registered host.</summary>
    Task<IReadOnlySet<string>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Removes a host's registration, if present.</summary>
    Task UnregisterAsync(string host, CancellationToken cancellationToken);
}

/// <summary>
/// Records, in the shared partition container, every host which serves the partitions assigned to it: silos with
/// automatic hosting under their silo addresses, and silos with an explicit <see cref="GarnetJournalStorageOptions.HostId"/>
/// under that identifier (D33).
/// </summary>
/// <remarks>
/// Each host is one empty blob under <see cref="Prefix"/>, a location no partition may use
/// (<see cref="GarnetPartitionBlobStorage.ValidateStorageLocations"/>). A host registers before its partitions start
/// and before it can become active in membership, so a host the failover agent finds active and unregistered does not
/// serve assignments under that name.
/// </remarks>
internal sealed class GarnetJournalHostRegistry(BlobContainerClient container) : IGarnetJournalHostRegistry
{
    /// <summary>The first segment of registration blob names, which partition storage locations may not use.</summary>
    internal const string ReservedSegment = "~hosts";

    private const string Prefix = ReservedSegment + "/";

    /// <summary>Registers <paramref name="host"/>.</summary>
    public async Task RegisterAsync(string host, CancellationToken cancellationToken)
    {
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await container.GetBlobClient(Prefix + Uri.EscapeDataString(host)).UploadAsync(BinaryData.Empty, overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UnregisterAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            await container.GetBlobClient(Prefix + Uri.EscapeDataString(host)).DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<string>> ListAsync(CancellationToken cancellationToken)
    {
        var hosts = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, Prefix, cancellationToken).ConfigureAwait(false))
            {
                hosts.Add(Uri.UnescapeDataString(item.Name[Prefix.Length..]));
            }
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            // The container does not exist yet, so no host has registered.
        }

        return hosts;
    }
}
