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
    public async Task DeleteAsync_AllowsNextAppendToRecreateRootWal()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var storage = CreateStorage(appendBlobs);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.DeleteAsync(CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        Assert.Equal(2, appendBlobs.CreateCalls.Count(call => call.Name == "blob.log.00000000"));
        Assert.Equal(2, appendBlobs.AppendCalls.Count(call => call.Name == "blob.log.00000000"));
    }

    [Fact]
    public async Task AppendAsync_WhenMimeTypeConfigured_SetsBlobContentType()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var storage = CreateStorage(appendBlobs, mimeType: "application/jsonl");

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        Assert.Equal("application/jsonl", appendBlobs.CreateCalls.Single().ContentType);
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
    public void DefaultWalAndCheckpointNames_UseFixedWidthHexIds()
    {
        var options = new AzureBlobJournalStorageOptions();
        var grainId = GrainId.Create("test-grain", "0");

        Assert.Equal("journals/test.log.0000000A", options.GetWalSegmentBlobName(new(grainId, "journals/test", 10)));
        Assert.Equal("journals/test.checkpoint.0000000B", options.GetCheckpointBlobName(new(grainId, "journals/test", 11)));
    }

    [Fact]
    public async Task AppendAsync_WhenCurrentWalIsSealed_RollsToNextWalSegment()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var storage = CreateStorage(appendBlobs);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        appendBlobs.Seal("blob.log.00000000");
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        Assert.Equal(["create:blob.log.00000000", "append:blob.log.00000000", "seal:blob.log.00000000", "append:blob.log.00000000", "seal:blob.log.00000000", "create:blob.log.00000001", "append:blob.log.00000001"], appendBlobs.Operations);
        Assert.Equal([2], appendBlobs.GetContent("blob.log.00000001"));
    }

    [Fact]
    public async Task ReplaceAsync_SealsWalUploadsImmutableCheckpointAndPublishesRootMetadata()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var checkpoints = new FakeBlockBlobStore();
        var storage = CreateStorage(appendBlobs, checkpoints, mimeType: "application/jsonl", journalFormatKey: "json-lines");

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2, 3]), CancellationToken.None);

        Assert.Contains("seal:blob.log.00000000", appendBlobs.Operations);
        var upload = checkpoints.UploadCalls.Single();
        Assert.Equal("blob.checkpoint.00000000", upload.Name);
        Assert.Equal(ETag.All, upload.IfNoneMatch);
        Assert.Equal([2, 3], upload.Payload);
        Assert.Equal("application/jsonl", upload.ContentType);
        Assert.Equal("json-lines", upload.Metadata[AzureBlobJournalStorage.FormatMetadataKey]);
        Assert.Equal("00000000", upload.Metadata[AzureBlobJournalStorage.CheckpointIdMetadataKey]);
        Assert.Equal("00000001", upload.Metadata[AzureBlobJournalStorage.CheckpointReplaySegmentMetadataKey]);
        Assert.Equal("0", upload.Metadata[AzureBlobJournalStorage.CheckpointReplayOffsetMetadataKey]);

        var metadata = appendBlobs.SetMetadataCalls.Single();
        Assert.Equal("blob.log.00000000", metadata.Name);
        Assert.Equal("blob.checkpoint.00000000", metadata.Metadata[AzureBlobJournalStorage.CheckpointMetadataKey]);
        Assert.Equal("\"checkpoint-1\"", metadata.Metadata[AzureBlobJournalStorage.CheckpointETagMetadataKey]);
        Assert.Equal("2", metadata.Metadata[AzureBlobJournalStorage.CheckpointLengthMetadataKey]);
        Assert.Equal("json-lines", metadata.Metadata[AzureBlobJournalStorage.CheckpointFormatMetadataKey]);
    }

    [Fact]
    public async Task ReadAsync_WhenNoCheckpointMetadata_ReadsRootWalOnly()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add("blob.log.00000000", [1, 2], metadata: new Dictionary<string, string>(), isSealed: false);
        var checkpoints = new FakeBlockBlobStore();
        var storage = CreateStorage(appendBlobs, checkpoints);

        var consumer = new CapturingJournalStorageConsumer();
        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Equal([1, 2], consumer.Bytes.ToArray());
        Assert.Single(appendBlobs.DownloadCalls);
        Assert.Empty(checkpoints.DownloadCalls);
    }

    [Fact]
    public async Task ReadAsync_WhenCheckpointMetadataPresent_ReadsCheckpointThenWalTail()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add(
            "blob.log.00000000",
            [9, 9],
            CheckpointMarkerMetadata("blob.checkpoint.00000000", new ETag("\"checkpoint-etag\""), "json-lines", 2, checkpointId: 0, replaySegment: 1, replayOffset: 0),
            isSealed: true);
        appendBlobs.Add("blob.log.00000001", [3, 4], new Dictionary<string, string> { [AzureBlobJournalStorage.FormatMetadataKey] = "json-lines" }, isSealed: false);
        var checkpoints = new FakeBlockBlobStore();
        checkpoints.Add(
            "blob.checkpoint.00000000",
            [1, 2],
            new ETag("\"checkpoint-etag\""),
            new Dictionary<string, string>
            {
                [AzureBlobJournalStorage.FormatMetadataKey] = "json-lines",
                [AzureBlobJournalStorage.CheckpointIdMetadataKey] = "00000000",
                [AzureBlobJournalStorage.CheckpointReplaySegmentMetadataKey] = "00000001",
                [AzureBlobJournalStorage.CheckpointReplayOffsetMetadataKey] = "0"
            });
        var storage = CreateStorage(appendBlobs, checkpoints);

        var consumer = new CapturingJournalStorageConsumer();
        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Equal("json-lines", consumer.JournalFormatKey);
        Assert.Equal([1, 2, 3, 4], consumer.Bytes.ToArray());
        Assert.Equal(["blob.log.00000000", "blob.log.00000001"], appendBlobs.DownloadCalls.Select(static call => call.Name));
        Assert.Single(checkpoints.DownloadCalls);
        Assert.Equal(new ETag("\"checkpoint-etag\""), checkpoints.DownloadCalls.Single().IfMatch);
    }

    [Fact]
    public async Task ReplaceAsync_WhenCheckpointUploadFails_DoesNotPublishRootMetadata()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var checkpoints = new FakeBlockBlobStore { FailNextUpload = true };
        var storage = CreateStorage(appendBlobs, checkpoints);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());

        Assert.Empty(appendBlobs.SetMetadataCalls);

        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);
        Assert.Equal([3], appendBlobs.GetContent("blob.log.00000001"));
    }

    [Fact]
    public async Task ReplaceAsync_WhenRootMetadataUpdateFails_DoesNotPublishCheckpoint()
    {
        var appendBlobs = new FakeAppendBlobStore { FailNextSetMetadata = true };
        var checkpoints = new FakeBlockBlobStore();
        var storage = CreateStorage(appendBlobs, checkpoints);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());

        Assert.Single(checkpoints.UploadCalls);
        Assert.Single(appendBlobs.SetMetadataCalls);

        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);
        Assert.Equal([3], appendBlobs.GetContent("blob.log.00000001"));
    }

    [Fact]
    public async Task ReadAsync_WhenCheckpointIsMissing_Throws()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add(
            "blob.log.00000000",
            [],
            CheckpointMarkerMetadata("missing-checkpoint", new ETag("\"missing\""), "json-lines", 1, checkpointId: 0, replaySegment: 1, replayOffset: 0),
            isSealed: true);
        var storage = CreateStorage(appendBlobs, new FakeBlockBlobStore());

        var exception = await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None).AsTask());
        Assert.Equal(404, exception.Status);
    }

    [Fact]
    public async Task ReadAsync_WhenCheckpointReplayOffsetExceedsWalLength_Throws()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add(
            "blob.log.00000000",
            [],
            CheckpointMarkerMetadata("blob.checkpoint.00000000", new ETag("\"checkpoint-etag\""), "json-lines", 1, checkpointId: 0, replaySegment: 1, replayOffset: 5),
            isSealed: true);
        appendBlobs.Add("blob.log.00000001", [1], new Dictionary<string, string> { [AzureBlobJournalStorage.FormatMetadataKey] = "json-lines" }, isSealed: false);
        var checkpoints = new FakeBlockBlobStore();
        checkpoints.Add(
            "blob.checkpoint.00000000",
            [2],
            new ETag("\"checkpoint-etag\""),
            new Dictionary<string, string>
            {
                [AzureBlobJournalStorage.FormatMetadataKey] = "json-lines",
                [AzureBlobJournalStorage.CheckpointIdMetadataKey] = "00000000",
                [AzureBlobJournalStorage.CheckpointReplaySegmentMetadataKey] = "00000001",
                [AzureBlobJournalStorage.CheckpointReplayOffsetMetadataKey] = "5"
            });
        var storage = CreateStorage(appendBlobs, checkpoints);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None).AsTask());
        Assert.Contains("offset", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_WhenCheckpointFormatMetadataIsMissing_Throws()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add(
            "blob.log.00000000",
            [],
            CheckpointMarkerMetadata("blob.checkpoint.00000000", new ETag("\"checkpoint-etag\""), "json-lines", 1, checkpointId: 0, replaySegment: 1, replayOffset: 0),
            isSealed: true);
        var checkpoints = new FakeBlockBlobStore();
        checkpoints.Add(
            "blob.checkpoint.00000000",
            [2],
            new ETag("\"checkpoint-etag\""),
            new Dictionary<string, string>
            {
                [AzureBlobJournalStorage.CheckpointIdMetadataKey] = "00000000",
                [AzureBlobJournalStorage.CheckpointReplaySegmentMetadataKey] = "00000001",
                [AzureBlobJournalStorage.CheckpointReplayOffsetMetadataKey] = "0"
            });
        var storage = CreateStorage(appendBlobs, checkpoints);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None).AsTask());
        Assert.Contains("format", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_UpdatesCompactionRequestFromCommittedBlockCount()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add("blob.log.00000000", [1], metadata: new Dictionary<string, string>(), isSealed: false, committedBlockCount: 11);
        var storage = CreateStorage(appendBlobs);

        await storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None);

        Assert.True(storage.IsCompactionRequested);
    }

    [Fact]
    public async Task AppendAsync_WhenJournalFormatKeyConfigured_StampsWalMetadata()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var storage = CreateStorage(appendBlobs, journalFormatKey: "json-lines");

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        var create = appendBlobs.CreateCalls.Single();
        Assert.True(create.Metadata.TryGetValue(AzureBlobJournalStorage.FormatMetadataKey, out var stamped));
        Assert.Equal("json-lines", stamped);
    }

    [Fact]
    public async Task AppendAsync_WhenAppendFails_DoesNotAdvanceAppendPosition()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var storage = CreateStorage(appendBlobs);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        appendBlobs.FailNextAppend = true;

        await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());
        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);

        Assert.Equal(1, appendBlobs.AppendCalls[1].AppendPosition);
        Assert.Equal(1, appendBlobs.AppendCalls[2].AppendPosition);
        Assert.Equal([3], appendBlobs.AppendCalls[2].Payload);
    }

    [Fact]
    public async Task AppendAsync_WhenBatchExceedsMaxAppendBlockBytes_ThrowsBeforeRoundTrip()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var storage = CreateStorage(appendBlobs);

        var oversize = OversizedSequence(AzureBlobJournalStorage.MaxAppendBlockBytes + 1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.AppendAsync(oversize, CancellationToken.None).AsTask());

        Assert.Contains("100 MiB", ex.Message);
        Assert.Contains("journal batch", ex.Message);
        Assert.Empty(appendBlobs.CreateCalls);
        Assert.Empty(appendBlobs.AppendCalls);
    }

    private static AzureBlobJournalStorage CreateStorage(
        FakeAppendBlobStore appendBlobs,
        FakeBlockBlobStore? checkpoints = null,
        string? mimeType = null,
        string? journalFormatKey = null)
    {
        checkpoints ??= new FakeBlockBlobStore();
        return new AzureBlobJournalStorage(
            appendBlobs.GetAppendBlobClient("blob.log.00000000"),
            mimeType,
            NullLogger<AzureBlobJournalStorage>.Instance,
            journalFormatKey,
            walNameFactory: segmentId => $"blob.log.{segmentId:X8}",
            walClientFactory: segmentId => appendBlobs.GetAppendBlobClient($"blob.log.{segmentId:X8}"),
            checkpointNameFactory: checkpointId => $"blob.checkpoint.{checkpointId:X8}",
            checkpointClientFactory: checkpoints.GetBlockBlobClient);
    }

    private static Dictionary<string, string> CheckpointMarkerMetadata(
        string checkpointName,
        ETag checkpointETag,
        string format,
        long checkpointLength,
        uint checkpointId,
        uint replaySegment,
        long replayOffset) => new()
    {
        [AzureBlobJournalStorage.FormatMetadataKey] = format,
        [AzureBlobJournalStorage.CheckpointMetadataKey] = checkpointName,
        [AzureBlobJournalStorage.CheckpointETagMetadataKey] = checkpointETag.ToString(),
        [AzureBlobJournalStorage.CheckpointFormatMetadataKey] = format,
        [AzureBlobJournalStorage.CheckpointIdMetadataKey] = checkpointId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
        [AzureBlobJournalStorage.CheckpointLengthMetadataKey] = checkpointLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
        [AzureBlobJournalStorage.CheckpointReplaySegmentMetadataKey] = replaySegment.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
        [AzureBlobJournalStorage.CheckpointReplayOffsetMetadataKey] = replayOffset.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static ReadOnlySequence<byte> OversizedSequence(long length)
    {
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

    private sealed class ChunkSegment : ReadOnlySequenceSegment<byte>
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

    private sealed class FakeAppendBlobStore
    {
        private readonly Dictionary<string, StoredAppendBlob> _blobs = [];
        private int _createCount;
        private int _appendCount;

        public bool FailNextAppend { get; set; }

        public bool FailNextSetMetadata { get; set; }

        public List<string> Operations { get; } = [];

        public List<CreateCall> CreateCalls { get; } = [];

        public List<AppendCall> AppendCalls { get; } = [];

        public List<SealCall> SealCalls { get; } = [];

        public List<SetMetadataCall> SetMetadataCalls { get; } = [];

        public List<DownloadCall> DownloadCalls { get; } = [];

        public AppendBlobClient GetAppendBlobClient(string name) => new FakeAppendBlobClient(this, name);

        public byte[] GetContent(string name) => _blobs[name].Content;

        public void Add(string name, byte[] content, IDictionary<string, string> metadata, bool isSealed, int? committedBlockCount = null)
            => _blobs[name] = new StoredAppendBlob(content, new ETag($"\"{name}-etag\""), new Dictionary<string, string>(metadata), ContentType: null, committedBlockCount ?? (content.Length == 0 ? 0 : 1), isSealed);

        public void Seal(string name)
        {
            Operations.Add($"seal:{name}");
            var blob = _blobs[name];
            blob.IsSealed = true;
        }

        private Task<Response<BlobContentInfo>> CreateAsync(string name, AppendBlobCreateOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add($"create:{name}");
            CreateCalls.Add(new(
                name,
                options.Conditions?.IfMatch ?? default,
                options.Conditions?.IfNoneMatch ?? default,
                options.HttpHeaders?.ContentType,
                options.Metadata is null ? new Dictionary<string, string>() : new Dictionary<string, string>(options.Metadata)));
            ThrowIfAppendConditionsFail(name, options.Conditions);

            _createCount++;
            var eTag = new ETag($"\"create-{_createCount}\"");
            _blobs[name] = new StoredAppendBlob(
                [],
                eTag,
                options.Metadata is null ? [] : new Dictionary<string, string>(options.Metadata),
                options.HttpHeaders?.ContentType,
                CommittedBlockCount: 0,
                IsSealed: false);
            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobContentInfo(eTag, default, null, null, null, null, 0),
                TestResponse.Instance));
        }

        private async Task<Response<BlobAppendInfo>> AppendBlockAsync(string name, Stream content, AppendBlobAppendBlockOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_blobs.TryGetValue(name, out var blob))
            {
                throw new RequestFailedException(404, "Blob does not exist.");
            }

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var payload = buffer.ToArray();
            Operations.Add($"append:{name}");
            AppendCalls.Add(new(
                name,
                options.Conditions?.IfMatch ?? default,
                options.Conditions?.IfNoneMatch ?? default,
                options.Conditions?.IfAppendPositionEqual,
                payload));

            if (blob.IsSealed)
            {
                throw new RequestFailedException(409, "The specified blob is sealed.", "BlobIsSealed", null);
            }

            ThrowIfAppendConditionsFail(name, options.Conditions);
            if (FailNextAppend)
            {
                FailNextAppend = false;
                throw new RequestFailedException(500, "Append failed.");
            }

            _appendCount++;
            blob.Content = [.. blob.Content, .. payload];
            blob.CommittedBlockCount++;
            blob.ETag = new ETag($"\"append-{_appendCount}\"");
            return Response.FromValue(
                BlobsModelFactory.BlobAppendInfo(blob.ETag, default, null, null, "0", blob.CommittedBlockCount, false, null, null),
                TestResponse.Instance);
        }

        private Task<Response<BlobInfo>> SealAsync(string name, AppendBlobRequestConditions? conditions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add($"seal:{name}");
            SealCalls.Add(new(name, conditions?.IfMatch ?? default));
            if (!_blobs.TryGetValue(name, out var blob))
            {
                throw new RequestFailedException(404, "Blob does not exist.");
            }

            ThrowIfAppendConditionsFail(name, conditions);
            blob.IsSealed = true;
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobInfo(blob.ETag, default), TestResponse.Instance));
        }

        private Task<Response<BlobInfo>> SetMetadataAsync(string name, IDictionary<string, string> metadata, BlobRequestConditions? conditions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add($"set-metadata:{name}");
            SetMetadataCalls.Add(new(name, conditions?.IfMatch ?? default, new Dictionary<string, string>(metadata)));
            if (!_blobs.TryGetValue(name, out var blob))
            {
                throw new RequestFailedException(404, "Blob does not exist.");
            }

            if (conditions?.IfMatch is { } ifMatch && ifMatch != default && ifMatch != blob.ETag)
            {
                throw new RequestFailedException(412, "ETag mismatch.");
            }

            if (FailNextSetMetadata)
            {
                FailNextSetMetadata = false;
                throw new RequestFailedException(500, "Set metadata failed.");
            }

            blob.Metadata = new Dictionary<string, string>(metadata);
            blob.ETag = new ETag($"\"metadata-{SetMetadataCalls.Count}\"");
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobInfo(blob.ETag, default), TestResponse.Instance));
        }

        private Task<Response> DeleteAsync(string name, BlobRequestConditions? conditions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_blobs.TryGetValue(name, out var blob))
            {
                throw new RequestFailedException(404, "Blob does not exist.");
            }

            if (conditions?.IfMatch is { } ifMatch && ifMatch != default && ifMatch != blob.ETag)
            {
                throw new RequestFailedException(412, "ETag mismatch.");
            }

            _blobs.Remove(name);
            return Task.FromResult<Response>(TestResponse.Instance);
        }

        private Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(string name, BlobDownloadOptions? options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCalls.Add(new(name));
            if (!_blobs.TryGetValue(name, out var blob))
            {
                throw new RequestFailedException(404, "Blob does not exist.");
            }

            var details = BlobsModelFactory.BlobDownloadDetails(
                blobType: BlobType.Append,
                contentLength: blob.Content.Length,
                contentType: blob.ContentType,
                metadata: blob.Metadata,
                blobCommittedBlockCount: blob.CommittedBlockCount,
                isSealed: blob.IsSealed,
                eTag: blob.ETag);
            var result = BlobsModelFactory.BlobDownloadStreamingResult(new MemoryStream(blob.Content), details);
            return Task.FromResult(Response.FromValue(result, TestResponse.Instance));
        }

        private void ThrowIfAppendConditionsFail(string name, AppendBlobRequestConditions? conditions)
        {
            if (conditions is null)
            {
                return;
            }

            var exists = _blobs.TryGetValue(name, out var blob);
            if (conditions.IfNoneMatch == ETag.All && exists)
            {
                throw new RequestFailedException(409, "Blob already exists.");
            }

            if (conditions.IfMatch is { } ifMatch && ifMatch != default && (!exists || ifMatch != blob!.ETag))
            {
                throw new RequestFailedException(412, "ETag mismatch.");
            }

            if (conditions.IfAppendPositionEqual is { } appendPosition && (!exists || appendPosition != blob!.Content.Length))
            {
                throw new RequestFailedException(412, "Append position mismatch.");
            }
        }

        private sealed class FakeAppendBlobClient(FakeAppendBlobStore store, string name) : AppendBlobClient
        {
            public override string BlobContainerName => "container";

            public override string Name => name;

            public override Task<Response<BlobContentInfo>> CreateAsync(AppendBlobCreateOptions options, CancellationToken cancellationToken = default)
                => store.CreateAsync(name, options, cancellationToken);

            public override Task<Response<BlobAppendInfo>> AppendBlockAsync(Stream content, AppendBlobAppendBlockOptions options, CancellationToken cancellationToken = default)
                => store.AppendBlockAsync(name, content, options, cancellationToken);

            public override Task<Response<BlobInfo>> SealAsync(AppendBlobRequestConditions conditions = default!, CancellationToken cancellationToken = default)
                => store.SealAsync(name, conditions, cancellationToken);

            public override Task<Response<BlobInfo>> SetMetadataAsync(IDictionary<string, string> metadata, BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
                => store.SetMetadataAsync(name, metadata, conditions, cancellationToken);

            public override Task<Response> DeleteAsync(DeleteSnapshotsOption snapshotsOption = DeleteSnapshotsOption.None, BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
                => store.DeleteAsync(name, conditions, cancellationToken);

            public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(BlobDownloadOptions? options = null, CancellationToken cancellationToken = default)
                => store.DownloadStreamingAsync(name, options, cancellationToken);
        }
    }

    private sealed class FakeBlockBlobStore
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
                throw new RequestFailedException(500, "Checkpoint upload failed.");
            }

            _uploadCount++;
            var eTag = new ETag($"\"checkpoint-{_uploadCount}\"");
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
                throw new RequestFailedException(404, "Checkpoint does not exist.");
            }

            if (ifMatch != default && ifMatch != blob.ETag)
            {
                throw new RequestFailedException(412, "Checkpoint ETag mismatch.");
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

    private sealed record CreateCall(string Name, ETag IfMatch, ETag IfNoneMatch, string? ContentType, IDictionary<string, string> Metadata);

    private sealed record AppendCall(string Name, ETag IfMatch, ETag IfNoneMatch, long? AppendPosition, byte[] Payload);

    private sealed record SealCall(string Name, ETag IfMatch);

    private sealed record SetMetadataCall(string Name, ETag IfMatch, IDictionary<string, string> Metadata);

    private sealed record DownloadCall(string Name);

    private sealed record BlockUploadCall(string Name, ETag IfMatch, ETag IfNoneMatch, string? ContentType, IDictionary<string, string> Metadata, byte[] Payload);

    private sealed record BlockDownloadCall(string Name, ETag IfMatch);

    private sealed class StoredAppendBlob(
        byte[] content,
        ETag eTag,
        IDictionary<string, string> metadata,
        string? ContentType,
        int CommittedBlockCount,
        bool IsSealed)
    {
        public byte[] Content { get; set; } = content;

        public ETag ETag { get; set; } = eTag;

        public IDictionary<string, string> Metadata { get; set; } = metadata;

        public string? ContentType { get; } = ContentType;

        public int CommittedBlockCount { get; set; } = CommittedBlockCount;

        public bool IsSealed { get; set; } = IsSealed;
    }

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
