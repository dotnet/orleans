using System.Collections.Immutable;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Orleans.Runtime;
using Orleans.Runtime.ClusterServices;
using Orleans.Runtime.GrainDirectory;

namespace Orleans.Persistence.AzureStorage;

/// <summary>
/// A single primary-endpoint Azure blob is the atomic service authority.
/// The caller provisions its container and a unique blob per service/authority before using this internal adapter.
/// Geo-redundant secondary reads are deliberately disallowed.
/// </summary>
internal sealed class AzureBlobClusterServiceViewRegister : IClusterServiceViewRegister
{
    private readonly BlobClient _blob;
    private readonly RegisteredServiceViewId _namespace;

    public AzureBlobClusterServiceViewRegister(
        string serviceId,
        string authorityId,
        Uri blobUri,
        BlobClientOptions? options = null,
        TokenCredential? credential = null)
    {
        ValidatePrimaryEndpoint(blobUri, options);
        _namespace = new(serviceId, authorityId, 0);
        _blob = credential is null ? new(blobUri, options) : new(blobUri, credential, options);
    }

    public AzureBlobClusterServiceViewRegister(
        string serviceId,
        string authorityId,
        string connectionString,
        string containerName,
        string blobName,
        BlobClientOptions? options = null)
    {
        _namespace = new(serviceId, authorityId, 0);
        _blob = new(connectionString, containerName, blobName, options);
        ValidatePrimaryEndpoint(_blob.Uri, options);
    }

    private static void ValidatePrimaryEndpoint(Uri blobUri, BlobClientOptions? options)
    {
        ArgumentNullException.ThrowIfNull(blobUri);
        if (blobUri.Host.Contains("-secondary.", StringComparison.OrdinalIgnoreCase)
            || options?.GeoRedundantSecondaryUri is not null)
        {
            throw new ArgumentException("A service-view register requires primary-only, strongly consistent blob access.", nameof(blobUri));
        }
    }

    public async ValueTask<ClusterServiceRegisterRead> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Content and ETag come from the same GET, not separate properties/content requests.
            var response = await _blob.DownloadContentAsync(cancellationToken);
            var stored = JsonSerializer.Deserialize<StoredView>(response.Value.Content.ToMemory().Span)
                ?? throw new InvalidDataException("The service-view blob contains no snapshot.");
            if (stored.FormatVersion != 1)
            {
                throw new InvalidDataException($"Unsupported service-view storage format '{stored.FormatVersion}'.");
            }

            var id = new RegisteredServiceViewId(stored.ServiceId, stored.AuthorityId, stored.Revision);
            _namespace.CompareTo(id);
            var view = new RegisteredClusterServiceView(
                id,
                stored.PredecessorRevision is { } predecessor ? new(stored.ServiceId, stored.AuthorityId, predecessor) : null,
                new(stored.MembershipWatermark),
                new(stored.ProtocolVersion, stored.ConfigurationFingerprint, stored.ConfigurationPayload, stored.PartitionsPerSilo),
                stored.Participants.Select(SiloAddress.FromParsableString),
                stored.Resources,
                stored.Assignments.Select(static assignment =>
                    new KeyValuePair<string, SiloAddress>(assignment.Resource, SiloAddress.FromParsableString(assignment.Owner))),
                stored.RingAssignments is { } ring
                    ? ring.Select(static assignment => new ClusterServicePartitionAssignment(
                        SiloAddress.FromParsableString(assignment.Owner),
                        assignment.Partition,
                        assignment.Full ? RingRange.Full : RingRange.Create(assignment.Start, assignment.End))).ToImmutableArray()
                    : default,
                stored.RingAssignments?.Select(static assignment => KeyValuePair.Create(
                    assignment.Resource,
                    assignment.Full ? RingRange.Full : RingRange.Create(assignment.Start, assignment.End))));
            return new(view, response.Value.Details.ETag.ToString());
        }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == BlobErrorCode.BlobNotFound.ToString())
        {
            return new(null, null);
        }
    }

    public async ValueTask<string?> TryWriteAsync(RegisteredClusterServiceView view, string? expectedToken, CancellationToken cancellationToken)
    {
        _namespace.CompareTo(view.Id);
        if ((expectedToken is null) != (view.Predecessor is null))
        {
            throw new ArgumentException("Initial creation requires absence; subsequent publications require the predecessor's CAS token.", nameof(expectedToken));
        }

        // Bind the proposed lineage to the actual snapshot carrying this ETag. The conditional PUT
        // still decides the race if another writer publishes after this primary read.
        var expected = await ReadAsync(cancellationToken);
        if (!StringComparer.Ordinal.Equals(expectedToken, expected.Token))
        {
            return null;
        }

        if (view.Predecessor != expected.View?.Id || view.Id.Revision != checked((expected.View?.Id.Revision ?? 0) + 1))
        {
            throw new ClusterServiceAuthorityException($"Publication '{view.Id}' does not follow the snapshot identified by its CAS token.");
        }

        var stored = new StoredView
        {
            FormatVersion = 1,
            ServiceId = view.Id.ServiceId,
            AuthorityId = view.Id.AuthorityId,
            Revision = view.Id.Revision,
            PredecessorRevision = view.Predecessor?.Revision,
            MembershipWatermark = view.MembershipWatermark.Value,
            ProtocolVersion = view.Configuration.ProtocolVersion,
            ConfigurationFingerprint = view.Configuration.Fingerprint,
            ConfigurationPayload = view.Configuration.Payload,
            PartitionsPerSilo = view.Configuration.PartitionsPerSilo,
            Participants = view.Participants.Select(static participant => participant.ToParsableString()).ToArray(),
            Resources = view.Resources.ToArray(),
            Assignments = view.ResourceOwners.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new StoredAssignment { Resource = pair.Key, Owner = pair.Value.ToParsableString() }).ToArray(),
            RingAssignments = view.RingAssignments.IsDefault ? null : view.ResourcePartitions.OrderBy(static entry => entry.Value.Range.Start).Select(static entry => new StoredRingAssignment
            {
                Resource = entry.Key,
                Owner = entry.Value.SiloAddress.ToParsableString(),
                Partition = entry.Value.PartitionIndex,
                Start = entry.Value.Range.Start,
                End = entry.Value.Range.End,
                Full = entry.Value.Range.IsFull
            }).ToArray()
        };
        var options = new BlobUploadOptions
        {
            Conditions = expectedToken is null
                ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                : new BlobRequestConditions { IfMatch = new ETag(expectedToken) },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
        };
        try
        {
            var response = await _blob.UploadAsync(BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(stored)), options, cancellationToken);
            return response.Value.ETag.ToString();
        }
        catch (RequestFailedException exception) when (exception.Status == 412
            || (exception.Status == 409 && exception.ErrorCode == BlobErrorCode.BlobAlreadyExists.ToString()))
        {
            return null;
        }
    }

    private sealed class StoredView
    {
        public int FormatVersion { get; init; }
        public required string ServiceId { get; init; }
        public required string AuthorityId { get; init; }
        public long Revision { get; init; }
        public long? PredecessorRevision { get; init; }
        public long MembershipWatermark { get; init; }
        public int ProtocolVersion { get; init; }
        public required string ConfigurationFingerprint { get; init; }
        public required string ConfigurationPayload { get; init; }
        public int PartitionsPerSilo { get; init; }
        public required string[] Participants { get; init; }
        public required string[] Resources { get; init; }
        public required StoredAssignment[] Assignments { get; init; }
        public StoredRingAssignment[]? RingAssignments { get; init; }
    }

    private sealed class StoredAssignment
    {
        public required string Resource { get; init; }
        public required string Owner { get; init; }
    }

    private sealed class StoredRingAssignment
    {
        public required string Resource { get; init; }
        public required string Owner { get; init; }
        public int Partition { get; init; }
        public uint Start { get; init; }
        public uint End { get; init; }
        public bool Full { get; init; }
    }
}
