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
        ArgumentNullException.ThrowIfNull(blobUri);
        if (blobUri.Host.Contains("-secondary.", StringComparison.OrdinalIgnoreCase)
            || options?.GeoRedundantSecondaryUri is not null)
        {
            throw new ArgumentException("A service-view register requires primary-only, strongly consistent blob access.", nameof(blobUri));
        }

        _namespace = new(serviceId, authorityId, 0);
        _blob = credential is null ? new(blobUri, options) : new(blobUri, credential, options);
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
                    : default);
            return new(view, response.Value.Details.ETag.ToString());
        }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == BlobErrorCode.BlobNotFound.ToString())
        {
            return new(null, null);
        }
    }

    public async ValueTask<bool> TryWriteAsync(RegisteredClusterServiceView view, string? expectedToken, CancellationToken cancellationToken)
    {
        _namespace.CompareTo(view.Id);
        if ((expectedToken is null) != (view.Predecessor is null))
        {
            throw new ArgumentException("Initial creation requires absence; subsequent publications require the predecessor's CAS token.", nameof(expectedToken));
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
            RingAssignments = view.RingAssignments.IsDefault ? null : view.RingAssignments.Select(static assignment => new StoredRingAssignment
            {
                Owner = assignment.SiloAddress.ToParsableString(),
                Partition = assignment.PartitionIndex,
                Start = assignment.Range.Start,
                End = assignment.Range.End,
                Full = assignment.Range.IsFull
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
            await _blob.UploadAsync(BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(stored)), options, cancellationToken);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status == 412
            || (exception.Status == 409 && exception.ErrorCode == BlobErrorCode.BlobAlreadyExists.ToString()))
        {
            return false;
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
        public required string Owner { get; init; }
        public int Partition { get; init; }
        public uint Start { get; init; }
        public uint End { get; init; }
        public bool Full { get; init; }
    }
}
