using System.Buffers;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class AzureAppendBlobLogStorageTests
{
    [Fact]
    public async Task DeleteAsync_AllowsNextAppendToRecreateBlob()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureAppendBlobLogStorage(client, NullLogger<AzureAppendBlobLogStorage>.Instance);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.DeleteAsync(CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        Assert.Equal(2, client.CreateCallCount);
        Assert.Equal(2, client.AppendCallCount);
    }

    [Fact]
    public async Task ReadAsync_RestoresEmptyBlobFromSnapshotUsingIfMatchCondition()
    {
        var emptyBlobETag = new ETag("\"empty\"");
        var client = new FakeAppendBlobClient();
        client.Downloads.Enqueue(new DownloadResult([], emptyBlobETag, new Dictionary<string, string> { ["snapshot"] = "snapshot-1" }));
        client.Downloads.Enqueue(new DownloadResult([1, 2, 3], new ETag("\"restored\""), new Dictionary<string, string>()));
        var storage = new AzureAppendBlobLogStorage(client, NullLogger<AzureAppendBlobLogStorage>.Instance);
        using var buffer = new ArcBufferWriter();

        await storage.ReadAsync(buffer, reader => reader.Skip(reader.Length), CancellationToken.None);

        Assert.Equal(1, client.CopyCallCount);
        Assert.NotNull(client.LastCopyOptions);
        Assert.Equal(emptyBlobETag, client.LastCopyOptions.DestinationConditions.IfMatch);
        Assert.Equal(default, client.LastCopyOptions.DestinationConditions.IfNoneMatch);
        Assert.Equal("snapshot-1", client.LastSnapshot);
    }

    private sealed class FakeAppendBlobClient : IAppendBlobClient
    {
        private readonly FakeAppendBlobClient _root;
        private readonly string? _snapshot;
        private bool _exists;
        private ETag _eTag = new("\"initial\"");

        public FakeAppendBlobClient()
        {
            _root = this;
        }

        private FakeAppendBlobClient(FakeAppendBlobClient root, string snapshot)
        {
            _root = root;
            _snapshot = snapshot;
        }

        public string BlobContainerName => "container";

        public string Name => "blob";

        public int CreateCallCount { get; private set; }

        public int AppendCallCount { get; private set; }

        public int CopyCallCount { get; private set; }

        public string? LastSnapshot { get; private set; }

        public BlobCopyFromUriOptions? LastCopyOptions { get; private set; }

        public Queue<DownloadResult> Downloads { get; } = new();

        public Task<Response<BlobContentInfo>> CreateAsync(AppendBlobCreateOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCallCount++;
            _exists = true;
            _eTag = new ETag($"\"create-{CreateCallCount}\"");
            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobContentInfo(_eTag, default, null, null, null, null, 0),
                TestResponse.Instance));
        }

        public async Task<Response<BlobAppendInfo>> AppendBlockAsync(Stream content, AppendBlobAppendBlockOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_exists)
            {
                throw new RequestFailedException(404, "Blob does not exist.");
            }

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            AppendCallCount++;
            _eTag = new ETag($"\"append-{AppendCallCount}\"");
            return Response.FromValue(
                BlobsModelFactory.BlobAppendInfo(_eTag, default, null, null, "0", AppendCallCount, false, null, null),
                TestResponse.Instance);
        }

        public Task DeleteAsync(BlobRequestConditions? conditions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _root._exists = false;
            return Task.CompletedTask;
        }

        public Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var download = Downloads.Dequeue();
            var details = BlobsModelFactory.BlobDownloadDetails(
                blobType: BlobType.Append,
                contentLength: download.Content.Length,
                metadata: download.Metadata,
                blobCommittedBlockCount: download.BlobCommittedBlockCount,
                eTag: download.ETag);
            var result = BlobsModelFactory.BlobDownloadStreamingResult(new MemoryStream(download.Content), details);
            return Task.FromResult(Response.FromValue(result, TestResponse.Instance));
        }

        public Task<Response<BlobCopyInfo>> SyncCopyFromUriAsync(Uri source, BlobCopyFromUriOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyCallCount++;
            LastCopyOptions = options;
            _eTag = new ETag($"\"copy-{CopyCallCount}\"");
            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobCopyInfo(_eTag, default, null, "copy", CopyStatus.Success),
                TestResponse.Instance));
        }

        public Task<Response<BlobSnapshotInfo>> CreateSnapshotAsync(BlobRequestConditions? conditions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobSnapshotInfo("snapshot-1", _eTag, default, null, false),
                TestResponse.Instance));
        }

        public IAppendBlobClient WithSnapshot(string snapshot)
        {
            _root.LastSnapshot = snapshot;
            return new FakeAppendBlobClient(_root, snapshot);
        }

        public Uri GenerateSasUri(BlobSasPermissions permissions, DateTimeOffset expiresOn)
            => new($"https://example.com/{Name}?snapshot={_snapshot}");
    }

    private sealed record DownloadResult(
        byte[] Content,
        ETag ETag,
        IDictionary<string, string> Metadata,
        int BlobCommittedBlockCount = 0);

    private sealed class TestResponse : Response
    {
        public static TestResponse Instance { get; } = new();

        public override int Status => 200;

        public override string ReasonPhrase => "OK";

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => false;

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];

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
