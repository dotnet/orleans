using System.Buffers;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class AzureBlobJournalStorageTests
{
    [Fact]
    public async Task DeleteAsync_AllowsNextAppendToRecreateBlob()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureBlobJournalStorage(client, NullLogger<AzureBlobJournalStorage>.Instance);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.DeleteAsync(CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        Assert.Equal(2, client.CreateCallCount);
        Assert.Equal(2, client.AppendCallCount);
    }

    [Fact]
    public async Task AppendAsync_WhenMimeTypeConfigured_SetsBlobContentType()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureBlobJournalStorage(client, "application/jsonl", NullLogger<AzureBlobJournalStorage>.Instance);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        Assert.Equal("application/jsonl", client.CreateCalls.Single().ContentType);
    }

    [Fact]
    public async Task AppendAsync_WhenMimeTypeUnavailable_LeavesBlobContentTypeUnset()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureBlobJournalStorage(client, mimeType: null, NullLogger<AzureBlobJournalStorage>.Instance);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        Assert.Null(client.CreateCalls.Single().ContentType);
    }

    [Fact]
    public void GetBlobNameForJournal_UsesConfiguredBlobNameWithoutAppendingFormatExtension()
    {
        var options = new AzureBlobJournalStorageOptions
        {
            GetBlobName = _ => "journals/test-grain"
        };

        var blobName = options.GetBlobNameForJournal(GrainId.Create("test-grain", "0"));

        Assert.Equal("journals/test-grain", blobName);
    }

    [Fact]
    public void GetBlobNameForJournal_ReturnsConfiguredBlobNameVerbatim()
    {
        var options = new AzureBlobJournalStorageOptions
        {
            GetBlobName = _ => "journals/test-grain.jsonl"
        };

        var blobName = options.GetBlobNameForJournal(GrainId.Create("test-grain", "0"));

        Assert.Equal("journals/test-grain.jsonl", blobName);
    }

    [Fact]
    public async Task ReplaceAsync_RecreatesAppendBlobAndAppendsCompactedState()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureBlobJournalStorage(client, "application/jsonl", NullLogger<AzureBlobJournalStorage>.Instance);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2, 3]), CancellationToken.None);

        Assert.Equal(2, client.CreateCallCount);
        Assert.Equal(2, client.AppendCallCount);

        var replaceCreate = client.CreateCalls[1];
        Assert.Equal(new ETag("\"append-1\""), replaceCreate.IfMatch);
        Assert.Equal(default, replaceCreate.IfNoneMatch);
        Assert.Empty(replaceCreate.Metadata);
        Assert.Equal("application/jsonl", replaceCreate.ContentType);

        var replaceAppend = client.AppendCalls[1];
        Assert.Equal(new ETag("\"create-2\""), replaceAppend.IfMatch);
        Assert.Equal([2, 3], replaceAppend.Payload);
    }

    [Fact]
    public async Task ReadAsync_DoesNotPerformSnapshotRecovery()
    {
        var client = new FakeAppendBlobClient();
        client.Downloads.Enqueue(new DownloadResult(
            [1, 2, 3],
            new ETag("\"read\""),
            new Dictionary<string, string>
            {
                ["snapshot"] = "snapshot-1",
                [AzureBlobJournalStorage.FormatMetadataKey] = "json-lines"
            },
            BlobCommittedBlockCount: 1));
        var storage = new AzureBlobJournalStorage(client, NullLogger<AzureBlobJournalStorage>.Instance);

        var consumer = new CapturingJournalStorageConsumer();
        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Equal("json-lines", consumer.JournalFormatKey);
        Assert.Empty(client.CreateCalls);
        Assert.Empty(client.AppendCalls);
    }

    [Fact]
    public async Task ReadAsync_UpdatesCompactionRequestFromCommittedBlockCount()
    {
        var client = new FakeAppendBlobClient();
        client.Downloads.Enqueue(new DownloadResult([1], new ETag("\"read\""), new Dictionary<string, string>(), BlobCommittedBlockCount: 11));
        var storage = new AzureBlobJournalStorage(client, NullLogger<AzureBlobJournalStorage>.Instance);

        await storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None);

        Assert.True(storage.IsCompactionRequested);

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        Assert.False(storage.IsCompactionRequested);
    }

    [Fact]
    public async Task AppendAsync_WhenJournalFormatKeyConfigured_StampsBlobMetadata()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            journalFormatKey: "json-lines");

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        var create = client.CreateCalls.Single();
        Assert.True(create.Metadata.TryGetValue(AzureBlobJournalStorage.FormatMetadataKey, out var stamped));
        Assert.Equal("json-lines", stamped);
    }

    [Fact]
    public async Task ReplaceAsync_WhenJournalFormatKeyConfigured_PreservesFormatKeyMetadata()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            journalFormatKey: "json-lines");

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        var replaceCreate = client.CreateCalls[1];
        Assert.Equal("json-lines", replaceCreate.Metadata[AzureBlobJournalStorage.FormatMetadataKey]);
    }

    [Fact]
    public async Task ReadAsync_WhenStoredFormatKeyDiffersFromConfigured_ReportsStoredFormatKey()
    {
        var client = new FakeAppendBlobClient();
        client.Downloads.Enqueue(new DownloadResult(
            [1, 2, 3],
            new ETag("\"read\""),
            new Dictionary<string, string> { [AzureBlobJournalStorage.FormatMetadataKey] = "orleans-binary" },
            BlobCommittedBlockCount: 1));
        var storage = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            journalFormatKey: "json-lines");

        var consumer = new CapturingJournalStorageConsumer();
        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Equal("orleans-binary", consumer.JournalFormatKey);
    }

    [Fact]
    public async Task ReadAsync_WhenStoredFormatKeyMatchesConfigured_Succeeds()
    {
        var client = new FakeAppendBlobClient();
        client.Downloads.Enqueue(new DownloadResult(
            [1, 2, 3],
            new ETag("\"read\""),
            new Dictionary<string, string> { [AzureBlobJournalStorage.FormatMetadataKey] = "json-lines" },
            BlobCommittedBlockCount: 1));
        var storage = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            journalFormatKey: "json-lines");

        var consumer = new CapturingJournalStorageConsumer();
        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Equal("json-lines", consumer.JournalFormatKey);
    }

    [Fact]
    public async Task ReadAsync_WhenLegacyBlobHasNoFormatKeyMetadata_AllowsReadForBackCompat()
    {
        var client = new FakeAppendBlobClient();
        client.Downloads.Enqueue(new DownloadResult([1, 2, 3], new ETag("\"read\""), new Dictionary<string, string>(), BlobCommittedBlockCount: 1));
        var storage = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            journalFormatKey: "json-lines");

        var consumer = new CapturingJournalStorageConsumer();
        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Null(consumer.JournalFormatKey);
    }

    [Fact]
    public async Task AppendAsync_WhenAppendFails_DoesNotAdvanceAppendCondition()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureBlobJournalStorage(client, NullLogger<AzureBlobJournalStorage>.Instance);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        client.FailNextAppend = true;

        await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());
        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);

        Assert.Equal(new ETag("\"append-1\""), client.AppendCalls[1].IfMatch);
        Assert.Equal(new ETag("\"append-1\""), client.AppendCalls[2].IfMatch);
        Assert.Equal([3], client.AppendCalls[2].Payload);
    }

    [Fact]
    public async Task AppendAsync_WhenBatchExceedsMaxAppendBlockBytes_ThrowsBeforeRoundTrip()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureBlobJournalStorage(client, NullLogger<AzureBlobJournalStorage>.Instance);

        var oversize = OversizedSequence(AzureBlobJournalStorage.MaxAppendBlockBytes + 1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.AppendAsync(oversize, CancellationToken.None).AsTask());

        Assert.Contains("100 MiB", ex.Message);
        Assert.Contains("journal batch", ex.Message);
        Assert.Equal(0, client.CreateCallCount);
        Assert.Equal(0, client.AppendCallCount);
    }

    [Fact]
    public async Task ReplaceAsync_WhenBatchExceedsMaxAppendBlockBytes_ThrowsBeforeRoundTrip()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureBlobJournalStorage(client, NullLogger<AzureBlobJournalStorage>.Instance);

        // Establish a successful create + append baseline before testing the replacement size guard.
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        var baselineCreates = client.CreateCallCount;
        var baselineAppends = client.AppendCallCount;

        var oversize = OversizedSequence(AzureBlobJournalStorage.MaxAppendBlockBytes + 1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReplaceAsync(oversize, CancellationToken.None).AsTask());

        Assert.Contains("100 MiB", ex.Message);
        Assert.Contains("compacted journal", ex.Message);
        Assert.Equal(baselineCreates, client.CreateCallCount);
        Assert.Equal(baselineAppends, client.AppendCallCount);
    }

    [Fact]
    public async Task AppendAsync_WhenApproachingBlockCeiling_Throws()
    {
        var client = new FakeAppendBlobClient { OverrideCommittedBlockCount = AzureBlobJournalStorage.MaxAppendBlobBlocks - 50 };
        var storage = new AzureBlobJournalStorage(client, NullLogger<AzureBlobJournalStorage>.Instance);

        // Prime the block counter via a successful append.
        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());

        Assert.Contains("maximum", ex.Message);
        Assert.Contains("50,000", ex.Message);
    }

    private static ReadOnlySequence<byte> OversizedSequence(long length)
    {
        // Build a multi-segment sequence that reports the given Length without allocating a single contiguous array.
        var segmentSize = 1 << 20;
        var first = new ChunkSegment(new byte[segmentSize], 0);
        var current = first;
        var remaining = length - segmentSize;
        while (remaining > 0)
        {
            var size = (int)Math.Min(segmentSize, remaining);
            current = current.Append(new byte[size]);
            remaining -= size;
        }

        return new ReadOnlySequence<byte>(first, 0, current, current.Memory.Length);
    }

    private sealed class ChunkSegment : System.Buffers.ReadOnlySequenceSegment<byte>
    {
        public ChunkSegment(byte[] buffer, long runningIndex)
        {
            Memory = buffer;
            RunningIndex = runningIndex;
        }

        public ChunkSegment Append(byte[] buffer)
        {
            var next = new ChunkSegment(buffer, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }

    private sealed class DiscardingJournalStorageConsumer : IJournalStorageConsumer
    {
        public static DiscardingJournalStorageConsumer Instance { get; } = new();

        public void Read(JournalBufferReader buffer, IJournalFileMetadata? metadata) => buffer.Skip(buffer.Length);
    }

    private sealed class CapturingJournalStorageConsumer : IJournalStorageConsumer
    {
        public string? JournalFormatKey { get; private set; }

        public void Read(JournalBufferReader buffer, IJournalFileMetadata? metadata)
        {
            JournalFormatKey = metadata?.Format;
            buffer.Skip(buffer.Length);
        }
    }

    private sealed class FakeAppendBlobClient : AppendBlobClient
    {
        private readonly FakeAppendBlobClient _root;
        private bool _exists;
        private ETag _eTag = new("\"initial\"");
        private int _successfulAppendCount;

        public FakeAppendBlobClient()
        {
            _root = this;
        }

        public override string BlobContainerName => "container";

        public override string Name => "blob";

        public int CreateCallCount => CreateCalls.Count;

        public int AppendCallCount => AppendCalls.Count;

        public bool FailNextAppend { get; set; }

        public int? OverrideCommittedBlockCount { get; set; }

        public List<CreateCall> CreateCalls { get; } = [];

        public List<AppendCall> AppendCalls { get; } = [];

        public Queue<DownloadResult> Downloads { get; } = new();

        public override Task<Response<BlobContentInfo>> CreateAsync(AppendBlobCreateOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _root.CreateCalls.Add(new(
                options.Conditions?.IfMatch ?? default,
                options.Conditions?.IfNoneMatch ?? default,
                options.HttpHeaders?.ContentType,
                options.Metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(options.Metadata)));
            _root._exists = true;
            _root._eTag = new ETag($"\"create-{_root.CreateCallCount}\"");
            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobContentInfo(_root._eTag, default, null, null, null, null, 0),
                TestResponse.Instance));
        }

        public override async Task<Response<BlobAppendInfo>> AppendBlockAsync(Stream content, AppendBlobAppendBlockOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_root._exists)
            {
                throw new RequestFailedException(404, "Blob does not exist.");
            }

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            _root.AppendCalls.Add(new(
                options.Conditions?.IfMatch ?? default,
                options.Conditions?.IfNoneMatch ?? default,
                buffer.ToArray()));
            if (_root.FailNextAppend)
            {
                _root.FailNextAppend = false;
                throw new RequestFailedException(500, "Append failed.");
            }

            _root._successfulAppendCount++;
            _root._eTag = new ETag($"\"append-{_root._successfulAppendCount}\"");
            var committedBlocks = _root.OverrideCommittedBlockCount ?? _root._successfulAppendCount;
            return Response.FromValue(
                BlobsModelFactory.BlobAppendInfo(_root._eTag, default, null, null, "0", committedBlocks, false, null, null),
                TestResponse.Instance);
        }

        public override Task<Response> DeleteAsync(DeleteSnapshotsOption snapshotsOption = DeleteSnapshotsOption.None, BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _root._exists = false;
            return Task.FromResult<Response>(TestResponse.Instance);
        }

        public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(BlobDownloadOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var download = _root.Downloads.Dequeue();
            var details = BlobsModelFactory.BlobDownloadDetails(
                blobType: BlobType.Append,
                contentLength: download.Content.Length,
                contentType: download.ContentType,
                metadata: download.Metadata,
                blobCommittedBlockCount: download.BlobCommittedBlockCount,
                eTag: download.ETag);
            var result = BlobsModelFactory.BlobDownloadStreamingResult(new MemoryStream(download.Content), details);
            return Task.FromResult(Response.FromValue(result, TestResponse.Instance));
        }

    }

    private sealed record DownloadResult(
        byte[] Content,
        ETag ETag,
        IDictionary<string, string> Metadata,
        int BlobCommittedBlockCount = 0,
        string? ContentType = null);

    private sealed record CreateCall(
        ETag IfMatch,
        ETag IfNoneMatch,
        string? ContentType,
        IDictionary<string, string> Metadata);

    private sealed record AppendCall(
        ETag IfMatch,
        ETag IfNoneMatch,
        byte[] Payload);

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
