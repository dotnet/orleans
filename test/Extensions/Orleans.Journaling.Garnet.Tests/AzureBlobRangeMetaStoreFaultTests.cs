using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Orleans.Journaling.Ranges;

namespace Orleans.Journaling.Garnet.Tests;

/// <summary>
/// Tests how <see cref="AzureBlobRangeMetaStore"/> classifies a conditional upload which fails its condition, by
/// injecting the blob's state when the failure is observed.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class AzureBlobRangeMetaStoreFaultTests
{
    private static readonly PartitionId P0 = new("p0");

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private static RangeMap CreateMap(long epoch)
        => RangeMap.CreateUniform(16, [new(P0, new PartitionHosting("host", epoch, "p0"))]) with { Id = "map" };

    [Fact]
    public async Task UploadWhichCommittedAndLostItsReply_IsReportedWritten()
    {
        // The upload commits this write's document, but the client sees the retry's condition failure.
        var blob = new FakeBlobClient(new RangeMapSnapshot(1, CreateMap(epoch: 0)));
        blob.OnUpload = content =>
        {
            blob.Store(content);
            throw new RequestFailedException(412, "Condition not met.", BlobErrorCode.ConditionNotMet.ToString(), innerException: null);
        };
        var store = new AzureBlobRangeMetaStore(blob);

        var result = await store.TryWriteAsync(CreateMap(epoch: 1), expectedVersion: 1, CancellationToken);

        Assert.True(result.Written);
        Assert.Equal(new RangeMapSnapshot(2, CreateMap(epoch: 1)), result.Current);
    }

    [Fact]
    public async Task IdenticalMapFromAnotherWriter_IsNotReportedWritten()
    {
        // Another writer stored the same map at the next version, under its own write id.
        var blob = new FakeBlobClient(new RangeMapSnapshot(1, CreateMap(epoch: 0)));
        blob.OnUpload = _ =>
        {
            blob.Store(RangeMapJsonSerializer.Serialize(new RangeMapSnapshot(2, CreateMap(epoch: 1)), writeId: "other-writer"));
            throw new RequestFailedException(412, "Condition not met.", BlobErrorCode.ConditionNotMet.ToString(), innerException: null);
        };
        var store = new AzureBlobRangeMetaStore(blob);

        var result = await store.TryWriteAsync(CreateMap(epoch: 1), expectedVersion: 1, CancellationToken);

        Assert.False(result.Written);
        Assert.Equal(new RangeMapSnapshot(2, CreateMap(epoch: 1)), result.Current);
    }

    [Fact]
    public async Task VersionBeyondTheNext_MakesTheOutcomeUnknown()
    {
        // This write may have committed version 2 before another writer committed version 3.
        var blob = new FakeBlobClient(new RangeMapSnapshot(1, CreateMap(epoch: 0)));
        blob.OnUpload = _ =>
        {
            blob.Store(RangeMapJsonSerializer.Serialize(new RangeMapSnapshot(3, CreateMap(epoch: 2)), writeId: "later-writer"));
            throw new RequestFailedException(412, "Condition not met.", BlobErrorCode.ConditionNotMet.ToString(), innerException: null);
        };
        var store = new AzureBlobRangeMetaStore(blob);

        var exception = await Assert.ThrowsAsync<IOException>(() => store.TryWriteAsync(CreateMap(epoch: 1), expectedVersion: 1, CancellationToken).AsTask());
        Assert.IsType<RequestFailedException>(exception.InnerException);
    }

    [Fact]
    public async Task FirstWriteWhichCommittedAndLostItsReply_IsReportedWritten()
    {
        // The first map's upload is conditioned on absence; its retry fails with 409 once the blob exists.
        var blob = new FakeBlobClient(current: null);
        blob.OnUpload = content =>
        {
            blob.Store(content);
            throw new RequestFailedException(409, "Blob already exists.", BlobErrorCode.BlobAlreadyExists.ToString(), innerException: null);
        };
        var store = new AzureBlobRangeMetaStore(blob);

        var result = await store.TryWriteAsync(CreateMap(epoch: 0), expectedVersion: 0, CancellationToken);

        Assert.True(result.Written);
        Assert.Equal(new RangeMapSnapshot(1, CreateMap(epoch: 0)), result.Current);
    }

    /// <summary>A blob held in memory, whose uploads run a test-supplied fault.</summary>
    private sealed class FakeBlobClient : BlobClient
    {
        private byte[]? _content;
        private int _version;

        public FakeBlobClient(RangeMapSnapshot? current)
        {
            if (current is not null)
            {
                Store(RangeMapJsonSerializer.Serialize(current, writeId: "initial"));
            }
        }

        public Action<byte[]>? OnUpload { get; set; }

        public void Store(byte[] content)
        {
            _content = content;
            _version++;
        }

        public override Task<Response<BlobDownloadResult>> DownloadContentAsync(CancellationToken cancellationToken = default)
        {
            if (_content is null)
            {
                throw new RequestFailedException(404, "The blob does not exist.", BlobErrorCode.BlobNotFound.ToString(), innerException: null);
            }

            var result = BlobsModelFactory.BlobDownloadResult(
                content: BinaryData.FromBytes(_content),
                details: BlobsModelFactory.BlobDownloadDetails(eTag: new ETag($"\"{_version}\"")));
            return Task.FromResult(Response.FromValue(result, new FakeResponse()));
        }

        public override Task<Response<BlobContentInfo>> UploadAsync(BinaryData content, BlobUploadOptions options, CancellationToken cancellationToken = default)
        {
            OnUpload?.Invoke(content.ToArray());
            Store(content.ToArray());
            var info = BlobsModelFactory.BlobContentInfo(new ETag($"\"{_version}\""), DateTimeOffset.UtcNow, contentHash: null, versionId: null, encryptionKeySha256: null, encryptionScope: null, blobSequenceNumber: 0);
            return Task.FromResult(Response.FromValue(info, new FakeResponse()));
        }
    }

    private sealed class FakeResponse : Response
    {
        public override int Status => 200;

        public override string ReasonPhrase => "OK";

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => false;

        protected override IEnumerable<global::Azure.Core.HttpHeader> EnumerateHeaders() => [];

        protected override bool TryGetHeader(string name, out string value)
        {
            value = string.Empty;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            values = [];
            return false;
        }
    }
}
