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
    public async Task ReplaceAsync_UploadsSnapshotThenReplacesAppendBlobMarker()
    {
        var client = new FakeAppendBlobClient();
        var snapshots = new FakeBlockBlobStore(client.Operations);
        var storage = new AzureBlobJournalStorage(
            client,
            "application/jsonl",
            NullLogger<AzureBlobJournalStorage>.Instance,
            snapshotNameFactory: () => "blob.snapshots/1",
            snapshotClientFactory: snapshots.GetBlockBlobClient);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2, 3]), CancellationToken.None);

        Assert.Equal(2, client.CreateCallCount);
        Assert.Equal(1, client.AppendCallCount);
        Assert.Single(snapshots.UploadCalls);
        Assert.Equal(["append-create", "append-block", "snapshot-upload", "append-create"], client.Operations);

        var snapshotUpload = snapshots.UploadCalls.Single();
        Assert.Equal("blob.snapshots/1", snapshotUpload.Name);
        Assert.Equal(ETag.All, snapshotUpload.IfNoneMatch);
        Assert.Equal([2, 3], snapshotUpload.Payload);
        Assert.Equal("application/jsonl", snapshotUpload.ContentType);

        var replaceCreate = client.CreateCalls[1];
        Assert.Equal(new ETag("\"append-1\""), replaceCreate.IfMatch);
        Assert.Equal(default, replaceCreate.IfNoneMatch);
        Assert.Equal("blob.snapshots/1", replaceCreate.Metadata[AzureBlobJournalStorage.SnapshotMetadataKey]);
        Assert.Equal("\"snapshot-1\"", replaceCreate.Metadata[AzureBlobJournalStorage.SnapshotETagMetadataKey]);
        Assert.Equal("2", replaceCreate.Metadata[AzureBlobJournalStorage.SnapshotLengthMetadataKey]);
        Assert.Equal("application/jsonl", replaceCreate.ContentType);
    }

    [Fact]
    public async Task ReadAsync_WhenSnapshotMetadataPresent_ReadsSnapshotThenAppendTail()
    {
        var client = new FakeAppendBlobClient();
        var snapshots = new FakeBlockBlobStore(client.Operations);
        snapshots.Add(
            "snapshot-1",
            [1, 2],
            new ETag("\"snapshot-etag\""),
            new Dictionary<string, string> { [AzureBlobJournalStorage.FormatMetadataKey] = "json-lines" });
        client.Downloads.Enqueue(new DownloadResult(
            [3, 4],
            new ETag("\"read\""),
            new Dictionary<string, string>
            {
                [AzureBlobJournalStorage.SnapshotMetadataKey] = "snapshot-1",
                [AzureBlobJournalStorage.SnapshotETagMetadataKey] = "\"snapshot-etag\"",
                [AzureBlobJournalStorage.SnapshotFormatMetadataKey] = "json-lines",
                [AzureBlobJournalStorage.SnapshotLengthMetadataKey] = "2",
                [AzureBlobJournalStorage.FormatMetadataKey] = "json-lines"
            },
            BlobCommittedBlockCount: 1));
        var storage = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            snapshotClientFactory: snapshots.GetBlockBlobClient);

        var consumer = new CapturingJournalStorageConsumer();
        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Equal("json-lines", consumer.JournalFormatKey);
        Assert.Equal([1, 2, 3, 4], consumer.Bytes.ToArray());
        Assert.Empty(client.CreateCalls);
        Assert.Empty(client.AppendCalls);
        Assert.Single(snapshots.DownloadCalls);
        Assert.Equal(new ETag("\"snapshot-etag\""), snapshots.DownloadCalls.Single().IfMatch);
    }

    [Fact]
    public async Task ReplaceAsync_WhenSnapshotUploadFails_DoesNotReplaceAppendBlobMarker()
    {
        var client = new FakeAppendBlobClient();
        var snapshots = new FakeBlockBlobStore(client.Operations) { FailNextUpload = true };
        var storage = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            snapshotNameFactory: () => "blob.snapshots/fail-upload",
            snapshotClientFactory: snapshots.GetBlockBlobClient);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());

        Assert.Equal(1, client.CreateCallCount);
        Assert.Single(snapshots.UploadCalls);

        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);
        Assert.Equal(new ETag("\"append-1\""), client.AppendCalls[1].IfMatch);
        Assert.Equal([3], client.AppendCalls[1].Payload);
    }

    [Fact]
    public async Task ReplaceAsync_WhenMarkerUpdateFails_DoesNotAdvanceAppendCondition()
    {
        var client = new FakeAppendBlobClient();
        var snapshots = new FakeBlockBlobStore(client.Operations);
        var storage = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            snapshotNameFactory: () => "blob.snapshots/marker-fail",
            snapshotClientFactory: snapshots.GetBlockBlobClient);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        client.FailNextCreate = true;

        await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());

        Assert.Single(snapshots.UploadCalls);
        Assert.Equal(2, client.CreateCallCount);

        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);
        Assert.Equal(new ETag("\"append-1\""), client.AppendCalls[1].IfMatch);
        Assert.Equal([3], client.AppendCalls[1].Payload);
    }

    [Fact]
    public async Task ReplaceAsync_WithStaleMarkerETag_ThrowsAndDoesNotRetry()
    {
        var client = new FakeAppendBlobClient();
        var snapshots = new FakeBlockBlobStore(client.Operations);
        var storageA = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            snapshotNameFactory: () => "blob.snapshots/a",
            snapshotClientFactory: snapshots.GetBlockBlobClient);
        var storageB = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            snapshotNameFactory: () => "blob.snapshots/b",
            snapshotClientFactory: snapshots.GetBlockBlobClient);

        await storageA.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storageB.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None);
        await storageA.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        var replaceException = await Assert.ThrowsAsync<RequestFailedException>(
            () => storageB.ReplaceAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None).AsTask());
        Assert.Equal(412, replaceException.Status);

        var appendException = await Assert.ThrowsAsync<RequestFailedException>(
            () => storageB.AppendAsync(new ReadOnlySequence<byte>([4]), CancellationToken.None).AsTask());
        Assert.Equal(412, appendException.Status);
        Assert.Equal(new ETag("\"append-1\""), client.CreateCalls.Last().IfMatch);
        Assert.Equal(new ETag("\"append-1\""), client.AppendCalls.Last().IfMatch);
        Assert.Equal(2, client.CreateCallCount);
        Assert.Single(snapshots.UploadCalls);
    }

    [Fact]
    public async Task ReadAsync_WhenSnapshotIsMissing_Throws()
    {
        var client = new FakeAppendBlobClient();
        var snapshots = new FakeBlockBlobStore(client.Operations);
        client.Downloads.Enqueue(new DownloadResult(
            [],
            new ETag("\"read\""),
            SnapshotMarkerMetadata("missing-snapshot", new ETag("\"missing-etag\""), "json-lines", 1),
            BlobCommittedBlockCount: 0));
        var storage = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            snapshotClientFactory: snapshots.GetBlockBlobClient);

        var exception = await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None).AsTask());
        Assert.Equal(404, exception.Status);
    }

    [Fact]
    public async Task ReadAsync_WhenSnapshotLengthMismatchesMarker_Throws()
    {
        var client = new FakeAppendBlobClient();
        var snapshots = new FakeBlockBlobStore(client.Operations);
        snapshots.Add(
            "snapshot-1",
            [1, 2],
            new ETag("\"snapshot-etag\""),
            new Dictionary<string, string> { [AzureBlobJournalStorage.FormatMetadataKey] = "json-lines" });
        client.Downloads.Enqueue(new DownloadResult(
            [],
            new ETag("\"read\""),
            SnapshotMarkerMetadata("snapshot-1", new ETag("\"snapshot-etag\""), "json-lines", 3),
            BlobCommittedBlockCount: 0));
        var storage = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            snapshotClientFactory: snapshots.GetBlockBlobClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None).AsTask());
        Assert.Contains("length", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_WhenSnapshotFormatDiffersFromTail_Throws()
    {
        var client = new FakeAppendBlobClient();
        var snapshots = new FakeBlockBlobStore(client.Operations);
        snapshots.Add(
            "snapshot-1",
            [1],
            new ETag("\"snapshot-etag\""),
            new Dictionary<string, string> { [AzureBlobJournalStorage.FormatMetadataKey] = "orleans-binary" });
        client.Downloads.Enqueue(new DownloadResult(
            [],
            new ETag("\"read\""),
            SnapshotMarkerMetadata("snapshot-1", new ETag("\"snapshot-etag\""), "json-lines", 1),
            BlobCommittedBlockCount: 0));
        var storage = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            snapshotClientFactory: snapshots.GetBlockBlobClient);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None).AsTask());
        Assert.Contains("format", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_UpdatesCompactionRequestFromCommittedBlockCount()
    {
        var client = new FakeAppendBlobClient();
        var snapshots = new FakeBlockBlobStore(client.Operations);
        client.Downloads.Enqueue(new DownloadResult([1], new ETag("\"read\""), new Dictionary<string, string>(), BlobCommittedBlockCount: 11));
        var storage = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            snapshotNameFactory: () => "blob.snapshots/compaction-request",
            snapshotClientFactory: snapshots.GetBlockBlobClient);

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
        var snapshots = new FakeBlockBlobStore(client.Operations);
        var storage = new AzureBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureBlobJournalStorage>.Instance,
            journalFormatKey: "json-lines",
            snapshotNameFactory: () => "blob.snapshots/format",
            snapshotClientFactory: snapshots.GetBlockBlobClient);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        var snapshotUpload = snapshots.UploadCalls.Single();
        Assert.Equal("json-lines", snapshotUpload.Metadata[AzureBlobJournalStorage.FormatMetadataKey]);

        var replaceCreate = client.CreateCalls[1];
        Assert.Equal("json-lines", replaceCreate.Metadata[AzureBlobJournalStorage.FormatMetadataKey]);
        Assert.Equal("json-lines", replaceCreate.Metadata[AzureBlobJournalStorage.SnapshotFormatMetadataKey]);
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

    private static Dictionary<string, string> SnapshotMarkerMetadata(string snapshotName, ETag snapshotETag, string format, long snapshotLength) => new()
    {
        [AzureBlobJournalStorage.FormatMetadataKey] = format,
        [AzureBlobJournalStorage.SnapshotMetadataKey] = snapshotName,
        [AzureBlobJournalStorage.SnapshotETagMetadataKey] = snapshotETag.ToString(),
        [AzureBlobJournalStorage.SnapshotFormatMetadataKey] = format,
        [AzureBlobJournalStorage.SnapshotLengthMetadataKey] = snapshotLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

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

        public MemoryStream Bytes { get; } = new();

        public void Read(JournalBufferReader buffer, IJournalFileMetadata? metadata)
        {
            JournalFormatKey = metadata?.Format;
            while (buffer.Length > 0)
            {
                var chunk = new byte[buffer.Length];
                buffer.Read(chunk);
                Bytes.Write(chunk);
            }
        }
    }

    private sealed class FakeAppendBlobClient : AppendBlobClient
    {
        private readonly FakeAppendBlobClient _root;
        private bool _exists;
        private ETag _eTag = new("\"initial\"");
        private int _successfulAppendCount;
        private int _committedBlockCount;
        private byte[] _content = [];
        private Dictionary<string, string> _metadata = [];
        private string? _contentType;

        public FakeAppendBlobClient()
        {
            _root = this;
        }

        public override string BlobContainerName => "container";

        public override string Name => "blob";

        public int CreateCallCount => CreateCalls.Count;

        public int AppendCallCount => AppendCalls.Count;

        public bool FailNextAppend { get; set; }

        public bool FailNextCreate { get; set; }

        public int? OverrideCommittedBlockCount { get; set; }

        public List<string> Operations { get; } = [];

        public List<CreateCall> CreateCalls { get; } = [];

        public List<AppendCall> AppendCalls { get; } = [];

        public Queue<DownloadResult> Downloads { get; } = new();

        public override Task<Response<BlobContentInfo>> CreateAsync(AppendBlobCreateOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _root.Operations.Add("append-create");
            _root.CreateCalls.Add(new(
                options.Conditions?.IfMatch ?? default,
                options.Conditions?.IfNoneMatch ?? default,
                options.HttpHeaders?.ContentType,
                options.Metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(options.Metadata)));
            _root.ThrowIfConditionsFail(options.Conditions);
            if (_root.FailNextCreate)
            {
                _root.FailNextCreate = false;
                throw new RequestFailedException(500, "Create failed.");
            }

            _root._exists = true;
            _root._content = [];
            _root._metadata = options.Metadata is null ? [] : new Dictionary<string, string>(options.Metadata);
            _root._contentType = options.HttpHeaders?.ContentType;
            _root._committedBlockCount = 0;
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
            _root.Operations.Add("append-block");
            _root.AppendCalls.Add(new(
                options.Conditions?.IfMatch ?? default,
                options.Conditions?.IfNoneMatch ?? default,
                buffer.ToArray()));
            _root.ThrowIfConditionsFail(options.Conditions);
            if (_root.FailNextAppend)
            {
                _root.FailNextAppend = false;
                throw new RequestFailedException(500, "Append failed.");
            }

            _root._successfulAppendCount++;
            _root._committedBlockCount++;
            _root._content = [.. _root._content, .. buffer.ToArray()];
            _root._eTag = new ETag($"\"append-{_root._successfulAppendCount}\"");
            var committedBlocks = _root.OverrideCommittedBlockCount ?? _root._committedBlockCount;
            return Response.FromValue(
                BlobsModelFactory.BlobAppendInfo(_root._eTag, default, null, null, "0", committedBlocks, false, null, null),
                TestResponse.Instance);
        }

        public override Task<Response> DeleteAsync(DeleteSnapshotsOption snapshotsOption = DeleteSnapshotsOption.None, BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (conditions?.IfMatch is { } ifMatch && ifMatch != default && ifMatch != _root._eTag)
            {
                throw new RequestFailedException(412, "ETag mismatch.");
            }

            _root._exists = false;
            return Task.FromResult<Response>(TestResponse.Instance);
        }

        public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(BlobDownloadOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_root._exists && _root.Downloads.Count == 0)
            {
                throw new RequestFailedException(404, "Blob does not exist.");
            }

            var download = _root.Downloads.Count > 0
                ? _root.Downloads.Dequeue()
                : new DownloadResult(
                    _root._content,
                    _root._eTag,
                    _root._metadata,
                    _root.OverrideCommittedBlockCount ?? _root._committedBlockCount,
                    _root._contentType);
            _root._exists = true;
            _root._eTag = download.ETag;
            _root._content = download.Content;
            _root._metadata = new Dictionary<string, string>(download.Metadata);
            _root._contentType = download.ContentType;
            _root._committedBlockCount = download.BlobCommittedBlockCount;
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

        private void ThrowIfConditionsFail(AppendBlobRequestConditions? conditions)
        {
            if (conditions is null)
            {
                return;
            }

            if (conditions.IfNoneMatch == ETag.All && _root._exists)
            {
                throw new RequestFailedException(409, "Blob already exists.");
            }

            if (conditions.IfMatch is { } ifMatch && ifMatch != default && ifMatch != _root._eTag)
            {
                throw new RequestFailedException(412, "ETag mismatch.");
            }
        }
    }

    private sealed class FakeBlockBlobStore(List<string> operations)
    {
        private readonly Dictionary<string, StoredBlockBlob> _blobs = [];
        private int _uploadCount;

        public bool FailNextUpload { get; set; }

        public List<BlockUploadCall> UploadCalls { get; } = [];

        public List<BlockDownloadCall> DownloadCalls { get; } = [];

        public BlockBlobClient GetBlockBlobClient(string name) => new FakeBlockBlobClient(this, name);

        public void Add(string name, byte[] content, ETag eTag, IDictionary<string, string> metadata)
            => _blobs[name] = new StoredBlockBlob(content, eTag, new Dictionary<string, string>(metadata), ContentType: null);

        private async Task<Response<BlobContentInfo>> UploadAsync(string name, Stream content, BlobUploadOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operations.Add("snapshot-upload");
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var payload = buffer.ToArray();
            UploadCalls.Add(new(
                name,
                options.Conditions?.IfMatch ?? default,
                options.Conditions?.IfNoneMatch ?? default,
                options.HttpHeaders?.ContentType,
                options.Metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(options.Metadata),
                payload));

            if (options.Conditions?.IfNoneMatch == ETag.All && _blobs.ContainsKey(name))
            {
                throw new RequestFailedException(409, "Blob already exists.");
            }

            if (FailNextUpload)
            {
                FailNextUpload = false;
                throw new RequestFailedException(500, "Snapshot upload failed.");
            }

            _uploadCount++;
            var eTag = new ETag($"\"snapshot-{_uploadCount}\"");
            _blobs[name] = new StoredBlockBlob(
                payload,
                eTag,
                options.Metadata is null ? [] : new Dictionary<string, string>(options.Metadata),
                options.HttpHeaders?.ContentType);
            return Response.FromValue(
                BlobsModelFactory.BlobContentInfo(eTag, default, null, null, null, null, payload.Length),
                TestResponse.Instance);
        }

        private Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(string name, BlobDownloadOptions? options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ifMatch = options?.Conditions?.IfMatch ?? default;
            DownloadCalls.Add(new(name, ifMatch));

            if (!_blobs.TryGetValue(name, out var blob))
            {
                throw new RequestFailedException(404, "Snapshot does not exist.");
            }

            if (ifMatch != default && ifMatch != blob.ETag)
            {
                throw new RequestFailedException(412, "Snapshot ETag mismatch.");
            }

            var details = BlobsModelFactory.BlobDownloadDetails(
                blobType: BlobType.Block,
                contentLength: blob.Content.Length,
                contentType: blob.ContentType,
                metadata: blob.Metadata,
                eTag: blob.ETag);
            var result = BlobsModelFactory.BlobDownloadStreamingResult(new MemoryStream(blob.Content), details);
            return Task.FromResult(Response.FromValue(result, TestResponse.Instance));
        }

        private sealed class FakeBlockBlobClient(FakeBlockBlobStore store, string name) : BlockBlobClient
        {
            public override string BlobContainerName => "container";

            public override string Name => name;

            public override Task<Response<BlobContentInfo>> UploadAsync(Stream content, BlobUploadOptions options, CancellationToken cancellationToken = default)
                => store.UploadAsync(name, content, options, cancellationToken);

            public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(BlobDownloadOptions? options = null, CancellationToken cancellationToken = default)
                => store.DownloadStreamingAsync(name, options, cancellationToken);
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

    private sealed record BlockUploadCall(
        string Name,
        ETag IfMatch,
        ETag IfNoneMatch,
        string? ContentType,
        IDictionary<string, string> Metadata,
        byte[] Payload);

    private sealed record BlockDownloadCall(string Name, ETag IfMatch);

    private sealed record StoredBlockBlob(byte[] Content, ETag ETag, IDictionary<string, string> Metadata, string? ContentType);

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
