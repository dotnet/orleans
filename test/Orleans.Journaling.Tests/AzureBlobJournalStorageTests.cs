using System.Buffers;
using System.Globalization;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Storage;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class AzureBlobJournalStorageTests
{
    [Fact]
    public async Task DeleteAsync_AllowsNextAppendToRecreateWal()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var storage = CreateStorage(appendBlobs);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.DeleteAsync(CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        Assert.Equal(2, appendBlobs.CreateCalls.Count(call => call.Name == "blob/wal"));
        Assert.Equal(2, appendBlobs.AppendCalls.Count(call => call.Name == "blob/wal"));
        Assert.Equal([2], appendBlobs.GetContent("blob/wal"));
    }

    [Fact]
    public async Task DeleteAsync_WhenWalETagChanges_DoesNotDeleteUpdatedWal()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var storage = CreateStorage(appendBlobs);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        appendBlobs.BeforeDelete = (name, conditions) =>
        {
            if (name == "blob/wal" && conditions?.IfMatch == new ETag("\"append-1\""))
            {
                appendBlobs.BeforeDelete = null;
                appendBlobs.Add("blob/wal", [1, 2], WalMetadata(), isSealed: false);
            }
        };

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.DeleteAsync(CancellationToken.None).AsTask());

        var requestFailed = Assert.IsType<RequestFailedException>(exception.InnerException);
        Assert.Equal(412, requestFailed.Status);

        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(appendBlobs).ReadAsync(consumer, CancellationToken.None);

        Assert.Equal([1, 2], consumer.Bytes.ToArray());
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

        var blobName = options.GetBlobNameForJournal(JournalId.FromGrainId(GrainId.Create("test-grain", "0")));

        Assert.Equal("journals/test-grain", blobName);
    }

    [Fact]
    public void GetBlobNameForJournal_UsesJournalIdOutsideGrain()
    {
        var options = new AzureBlobJournalStorageOptions();

        var blobName = options.GetBlobNameForJournal(new JournalId("journals/on-demand"));

        Assert.Equal("journals/on-demand", blobName);
    }

    [Fact]
    public void DefaultWalAndCheckpointNames_UseFixedWalAndSnapshotPrefix()
    {
        Assert.Equal("journals/test/wal", AzureBlobJournalStorageOptions.GetDefaultWalBlobName("journals/test"));
        Assert.Equal("journals/test/chk.snapshot", AzureBlobJournalStorageOptions.GetDefaultCheckpointBlobName("journals/test", "snapshot"));
    }

    [Fact]
    public async Task AppendAsync_WhenCurrentWalIsSealed_RequiresRecovery()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var storage = CreateStorage(appendBlobs);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        appendBlobs.Seal("blob/wal");

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());

        Assert.Contains("recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(appendBlobs.Operations, static operation => operation.Contains("seg.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplaceAsync_UploadsImmutableCheckpointAndPublishesWalMetadata()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var checkpoints = new FakeBlockBlobStore();
        var storage = CreateStorage(appendBlobs, checkpoints, mimeType: "application/jsonl", journalFormatKey: "json-lines");

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2, 3]), CancellationToken.None);

        var upload = checkpoints.UploadCalls.Single();
        Assert.StartsWith("blob/chk.", upload.Name);
        Assert.Equal(ETag.All, upload.IfNoneMatch);
        Assert.Equal([2, 3], upload.Payload);
        Assert.Equal("application/jsonl", upload.ContentType);
        AssertAzureMetadataKeys(upload.Metadata);
        Assert.Equal("json-lines", upload.Metadata[AzureBlobJournalStorage.FormatMetadataKey]);
        Assert.DoesNotContain("generation", upload.Metadata.Keys);

        var walPublish = appendBlobs.CreateCalls.Last();
        Assert.Equal("blob/wal", walPublish.Name);
        Assert.Equal(new ETag("\"append-1\""), walPublish.IfMatch);
        AssertAzureMetadataKeys(walPublish.Metadata);
        Assert.Equal("json-lines", walPublish.Metadata[AzureBlobJournalStorage.FormatMetadataKey]);
        Assert.Equal(upload.Name, walPublish.Metadata[AzureBlobJournalStorage.CheckpointMetadataKey]);
        Assert.Equal("0", walPublish.Metadata[AzureBlobJournalStorage.CheckpointOffsetMetadataKey]);
        Assert.DoesNotContain("generation", walPublish.Metadata.Keys);
        Assert.DoesNotContain("checkpoint_etag", walPublish.Metadata.Keys);
        Assert.DoesNotContain("checkpoint_length", walPublish.Metadata.Keys);
        Assert.DoesNotContain("checkpoint_format", walPublish.Metadata.Keys);

        await storage.AppendAsync(new ReadOnlySequence<byte>([4]), CancellationToken.None);
        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(appendBlobs, checkpoints).ReadAsync(consumer, CancellationToken.None);

        Assert.Equal([2, 3, 4], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReadAsync_WhenNoCheckpointMetadata_ReadsWalOnly()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add("blob/wal", [1, 2], metadata: WalMetadata(), isSealed: false);
        var checkpoints = new FakeBlockBlobStore();
        var storage = CreateStorage(appendBlobs, checkpoints);

        var consumer = new CapturingJournalStorageConsumer();
        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Equal([1, 2], consumer.Bytes.ToArray());
        Assert.Equal(["blob/wal"], appendBlobs.DownloadCalls.Select(static call => call.Name));
        Assert.Empty(checkpoints.DownloadCalls);
    }

    [Fact]
    public async Task ReadAsync_WhenCheckpointMetadataPresent_ReadsCheckpointThenWalTail()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add(
            "blob/wal",
            [9, 8, 3, 4],
            CheckpointMarkerMetadata("blob/chk.snapshot", "json-lines", checkpointOffset: 2),
            isSealed: false);
        var checkpoints = new FakeBlockBlobStore();
        checkpoints.Add(
            "blob/chk.snapshot",
            [1, 2],
            new ETag("\"checkpoint-etag\""),
            new Dictionary<string, string>
            {
                [AzureBlobJournalStorage.FormatMetadataKey] = "json-lines",
            });
        var storage = CreateStorage(appendBlobs, checkpoints);

        var consumer = new CapturingJournalStorageConsumer();
        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Equal("json-lines", consumer.JournalFormatKey);
        Assert.Equal([1, 2, 3, 4], consumer.Bytes.ToArray());
        Assert.Equal(["blob/wal"], appendBlobs.DownloadCalls.Select(static call => call.Name));
        Assert.Single(checkpoints.DownloadCalls);
        Assert.Equal(default(ETag), checkpoints.DownloadCalls.Single().IfMatch);
    }

    [Fact]
    public async Task ReplaceAsync_WhenCheckpointUploadFails_DoesNotPublishWalMetadata()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var checkpoints = new FakeBlockBlobStore { FailNextUpload = true };
        var storage = CreateStorage(appendBlobs, checkpoints);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());

        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);
        Assert.Equal([1, 3], appendBlobs.GetContent("blob/wal"));
        Assert.Empty(appendBlobs.SetMetadataCalls);
    }

    [Fact]
    public async Task ReplaceAsync_WhenWalPublishFails_DoesNotResetWal()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var checkpoints = new FakeBlockBlobStore();
        var storage = CreateStorage(appendBlobs, checkpoints);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        appendBlobs.FailNextCreate = true;
        await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());

        Assert.Single(checkpoints.UploadCalls);

        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);
        Assert.Equal([1, 3], appendBlobs.GetContent("blob/wal"));
    }

    [Fact]
    public async Task ReplaceAsync_WhenWalETagConflicts_RequiresRecovery()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var checkpoints = new FakeBlockBlobStore();
        var storage = CreateStorage(appendBlobs, checkpoints);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        appendBlobs.Add("blob/wal", [1], WalMetadata(), isSealed: false);

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());

        var requestFailed = Assert.IsType<RequestFailedException>(exception.InnerException);
        Assert.Equal(412, requestFailed.Status);
        Assert.Contains("recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplaceAsync_WhenWalETagChanges_DoesNotUploadStaleCheckpoint()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var checkpoints = new FakeBlockBlobStore();
        var storage = CreateStorage(appendBlobs, checkpoints);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        appendBlobs.Add("blob/wal", [1, 2], WalMetadata(), isSealed: false);

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.ReplaceAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None).AsTask());

        var requestFailed = Assert.IsType<RequestFailedException>(exception.InnerException);
        Assert.Equal(412, requestFailed.Status);
        Assert.Empty(checkpoints.UploadCalls);

        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(appendBlobs, checkpoints).ReadAsync(consumer, CancellationToken.None);

        Assert.Equal([1, 2], consumer.Bytes.ToArray());
        Assert.Empty(checkpoints.DownloadCalls);
    }

    [Fact]
    public async Task ReplaceAsync_WhenWalNearBlockLimit_ResetsBlockBudgetAndAllowsMoreAppends()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add("blob/wal", [1], metadata: WalMetadata(), isSealed: false, committedBlockCount: 49_900);
        var checkpoints = new FakeBlockBlobStore();
        var storage = CreateStorage(appendBlobs, checkpoints);

        await storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None);
        Assert.True(storage.IsCompactionRequested);

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);
        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);

        Assert.False(storage.IsCompactionRequested);
        Assert.Equal([3], appendBlobs.GetContent("blob/wal"));
        var consumer = new CapturingJournalStorageConsumer();
        await CreateStorage(appendBlobs, checkpoints).ReadAsync(consumer, CancellationToken.None);
        Assert.Equal([2, 3], consumer.Bytes.ToArray());
    }

    [Fact]
    public async Task ReplaceAsync_WhenReplacingExistingCheckpoint_DeletesPreviousCheckpointAfterPublishingNewWal()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var checkpoints = new FakeBlockBlobStore();
        var storage = CreateStorage(appendBlobs, checkpoints);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);
        var previousCheckpoint = checkpoints.UploadCalls.Single().Name;

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);

        var currentCheckpoint = checkpoints.UploadCalls.Last().Name;
        Assert.NotEqual(previousCheckpoint, currentCheckpoint);
        Assert.False(checkpoints.Exists(previousCheckpoint));
        Assert.True(checkpoints.Exists(currentCheckpoint));
        Assert.Equal([previousCheckpoint], checkpoints.DeleteCalls.Select(static call => call.Name));
    }

    [Fact]
    public async Task ReplaceAsync_WhenOldCheckpointCleanupDisabled_DoesNotReadWalManifestOrDeletePreviousCheckpoint()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var checkpoints = new FakeBlockBlobStore();
        var storage = CreateStorage(appendBlobs, checkpoints, deleteOldCheckpoints: false);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);
        var previousCheckpoint = checkpoints.UploadCalls.Single().Name;
        appendBlobs.PropertiesCalls.Clear();

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);

        Assert.Empty(appendBlobs.PropertiesCalls);
        Assert.True(checkpoints.Exists(previousCheckpoint));
        Assert.Empty(checkpoints.DeleteCalls);
    }

    [Fact]
    public async Task DeleteAsync_WhenWalExistsOnFreshStorage_DeletesWalAndReferencedCheckpoint()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add(
            "blob/wal",
            [1],
            CheckpointMarkerMetadata("blob/chk.snapshot", "json-lines"),
            isSealed: false);
        var checkpoints = new FakeBlockBlobStore();
        checkpoints.Add(
            "blob/chk.snapshot",
            [2],
            new ETag("\"checkpoint-etag\""),
            new Dictionary<string, string>
            {
                [AzureBlobJournalStorage.FormatMetadataKey] = "json-lines",
            });

        await CreateStorage(appendBlobs, checkpoints).DeleteAsync(CancellationToken.None);

        Assert.False(appendBlobs.Exists("blob/wal"));
        Assert.False(checkpoints.Exists("blob/chk.snapshot"));
    }

    [Fact]
    public async Task ReadAsync_WhenCheckpointIsMissing_Throws()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add(
            "blob/wal",
            [],
            CheckpointMarkerMetadata("missing-checkpoint", "json-lines"),
            isSealed: false);
        var storage = CreateStorage(appendBlobs, new FakeBlockBlobStore());

        var exception = await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None).AsTask());
        Assert.Equal(404, exception.Status);
    }

    [Fact]
    public async Task ReadAsync_WhenCheckpointFormatMetadataIsMissing_Throws()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add(
            "blob/wal",
            [],
            CheckpointMarkerMetadata("blob/chk.snapshot", "json-lines"),
            isSealed: false);
        var checkpoints = new FakeBlockBlobStore();
        checkpoints.Add(
            "blob/chk.snapshot",
            [2],
            new ETag("\"checkpoint-etag\""),
            new Dictionary<string, string>());
        var storage = CreateStorage(appendBlobs, checkpoints);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None).AsTask());
        Assert.Contains("format", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_WhenCheckpointFormatDiffersFromWal_Throws()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add(
            "blob/wal",
            [],
            CheckpointMarkerMetadata("blob/chk.snapshot", "json-lines"),
            isSealed: false);
        var checkpoints = new FakeBlockBlobStore();
        checkpoints.Add(
            "blob/chk.snapshot",
            [1, 2],
            new ETag("\"checkpoint-etag\""),
            new Dictionary<string, string>
            {
                [AzureBlobJournalStorage.FormatMetadataKey] = "binary",
            });
        var storage = CreateStorage(appendBlobs, checkpoints);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None).AsTask());

        Assert.Contains("format", exception.Message);
        Assert.Contains("binary", exception.Message);
        Assert.Contains("json-lines", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_UpdatesCompactionRequestFromCommittedBlockCount()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add("blob/wal", [1], metadata: WalMetadata(), isSealed: false, committedBlockCount: 49_001);
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
    public async Task AppendAsync_WhenAppendFails_ReusesLastObservedETag()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var storage = CreateStorage(appendBlobs);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        appendBlobs.FailNextAppend = true;

        await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());
        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);

        Assert.Equal(new ETag("\"append-1\""), appendBlobs.AppendCalls[1].IfMatch);
        Assert.Equal(new ETag("\"append-1\""), appendBlobs.AppendCalls[2].IfMatch);
        Assert.Equal([3], appendBlobs.AppendCalls[2].Payload);
    }

    [Fact]
    public async Task AppendAsync_WithoutPriorRead_LoadsExistingWal()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var first = CreateStorage(appendBlobs);
        await first.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        var second = CreateStorage(appendBlobs);
        await second.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        Assert.Equal([1, 2], appendBlobs.GetContent("blob/wal"));
        Assert.Contains(appendBlobs.PropertiesCalls, static call => call.Name == "blob/wal");
    }

    [Fact]
    public async Task AppendAsync_WhenWalETagConflicts_RequiresRecovery()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var storage = CreateStorage(appendBlobs);

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        appendBlobs.Add("blob/wal", [1, 2], WalMetadata(), isSealed: false);

        var exception = await Assert.ThrowsAsync<InconsistentStateException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None).AsTask());

        var requestFailed = Assert.IsType<RequestFailedException>(exception.InnerException);
        Assert.Equal(412, requestFailed.Status);
        Assert.Contains("recovery", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppendAsync_WhenWalNearBlockLimit_RequiresCompactionInsteadOfRolling()
    {
        var appendBlobs = new FakeAppendBlobStore();
        appendBlobs.Add("blob/wal", [1], metadata: WalMetadata(), isSealed: false, committedBlockCount: 49_900);
        var storage = CreateStorage(appendBlobs);

        await storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());

        Assert.Contains("compacted", exception.Message);
        Assert.Empty(appendBlobs.AppendCalls);
        Assert.DoesNotContain(appendBlobs.Operations, static operation => operation.Contains("seg.", StringComparison.Ordinal));
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

    [Fact]
    public async Task ReadAsync_WhenWalDoesNotExist_CompletesEmpty()
    {
        var appendBlobs = new FakeAppendBlobStore();
        var checkpoints = new FakeBlockBlobStore();
        var storage = CreateStorage(appendBlobs, checkpoints);

        var consumer = new CapturingJournalStorageConsumer();
        await storage.ReadAsync(consumer, CancellationToken.None);

        Assert.Empty(consumer.Bytes.ToArray());
        Assert.Empty(checkpoints.DownloadCalls);
    }

    private static AzureBlobJournalStorage CreateStorage(
        FakeAppendBlobStore appendBlobs,
        FakeBlockBlobStore? checkpoints = null,
        string? mimeType = null,
        string? journalFormatKey = null,
        bool deleteOldCheckpoints = true)
    {
        checkpoints ??= new FakeBlockBlobStore();
        return new AzureBlobJournalStorage(
            new AzureBlobJournalStorage.AzureBlobJournalStorageShared(
                NullLogger<AzureBlobJournalStorage>.Instance,
                Options.Create(new AzureBlobJournalStorageOptions { DeleteOldCheckpoints = deleteOldCheckpoints }),
                new FakeBlobClientProvider(appendBlobs, checkpoints),
                mimeType,
                journalFormatKey),
            JournalId.FromGrainId(GrainId.Create("test-grain", "0")));
    }

    private static Dictionary<string, string> WalMetadata(string? format = null)
    {
        var result = new Dictionary<string, string>();
        if (format is not null)
        {
            result[AzureBlobJournalStorage.FormatMetadataKey] = format;
        }

        return result;
    }

    private static Dictionary<string, string> CheckpointMarkerMetadata(
        string checkpointName,
        string format,
        long? checkpointOffset = null)
    {
        var result = new Dictionary<string, string>
        {
            [AzureBlobJournalStorage.FormatMetadataKey] = format,
            [AzureBlobJournalStorage.CheckpointMetadataKey] = checkpointName,
        };

        if (checkpointOffset is { } offset)
        {
            result[AzureBlobJournalStorage.CheckpointOffsetMetadataKey] = offset.ToString(CultureInfo.InvariantCulture);
        }

        return result;
    }

    private static void AssertAzureMetadataKeys(IDictionary<string, string> metadata)
        => Assert.All(metadata.Keys, key => Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", key));

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

    private sealed class FakeBlobClientProvider(FakeAppendBlobStore appendBlobs, FakeBlockBlobStore blockBlobs) : AzureBlobJournalStorage.BlobClientProvider
    {
        private const string JournalBlobName = "blob";

        public override AppendBlobClient GetWalClient(JournalId journalId)
            => appendBlobs.GetAppendBlobClient(AzureBlobJournalStorageOptions.GetDefaultWalBlobName(JournalBlobName));

        public override string GetCheckpointName(JournalId journalId, string snapshotId)
            => AzureBlobJournalStorageOptions.GetDefaultCheckpointBlobName(JournalBlobName, snapshotId);

        public override BlockBlobClient GetCheckpointClient(JournalId journalId, string checkpointName)
            => blockBlobs.GetBlockBlobClient(checkpointName);
    }

    private sealed class FakeAppendBlobStore
    {
        private readonly Dictionary<string, StoredAppendBlob> _blobs = [];
        private int _createCount;
        private int _appendCount;
        private int _sealCount;
        private int _metadataCount;

        public bool FailNextAppend { get; set; }

        public bool FailNextCreate { get; set; }

        public bool FailNextSetMetadata { get; set; }

        public Action<string, AppendBlobCreateOptions>? BeforeCreate { get; set; }

        public Action<string, IDictionary<string, string>, BlobRequestConditions?>? BeforeSetMetadata { get; set; }

        public Action<string, BlobRequestConditions?>? BeforeDelete { get; set; }

        public List<string> Operations { get; } = [];

        public List<CreateCall> CreateCalls { get; } = [];

        public List<AppendCall> AppendCalls { get; } = [];

        public List<SealCall> SealCalls { get; } = [];

        public List<SetMetadataCall> SetMetadataCalls { get; } = [];

        public List<DeleteCall> DeleteCalls { get; } = [];

        public List<DownloadCall> DownloadCalls { get; } = [];

        public List<GetPropertiesCall> PropertiesCalls { get; } = [];

        public AppendBlobClient GetAppendBlobClient(string name) => new FakeAppendBlobClient(this, name);

        public byte[] GetContent(string name) => _blobs[name].Content;

        public bool Exists(string name) => _blobs.ContainsKey(name);

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
            BeforeCreate?.Invoke(name, options);
            ThrowIfConditionsFail(name, options.Conditions);

            if (FailNextCreate)
            {
                FailNextCreate = false;
                throw new RequestFailedException(500, "Create failed.");
            }

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
                payload));

            if (blob.IsSealed)
            {
                throw new RequestFailedException(409, "The specified blob is sealed.", "BlobIsSealed", null);
            }

            ThrowIfConditionsFail(name, options.Conditions);
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

            ThrowIfConditionsFail(name, conditions);
            blob.IsSealed = true;
            _sealCount++;
            blob.ETag = new ETag($"\"seal-{_sealCount}\"");
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobInfo(blob.ETag, default), TestResponse.Instance));
        }

        private Task<Response<BlobInfo>> SetMetadataAsync(string name, IDictionary<string, string> metadata, BlobRequestConditions? conditions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add($"setMetadata:{name}");
            SetMetadataCalls.Add(new(
                name,
                conditions?.IfMatch ?? default,
                conditions?.IfNoneMatch ?? default,
                new Dictionary<string, string>(metadata)));
            BeforeSetMetadata?.Invoke(name, metadata, conditions);
            if (!_blobs.TryGetValue(name, out var blob))
            {
                throw new RequestFailedException(404, "Blob does not exist.");
            }

            ThrowIfConditionsFail(name, conditions);
            if (FailNextSetMetadata)
            {
                FailNextSetMetadata = false;
                throw new RequestFailedException(500, "Set metadata failed.");
            }

            _metadataCount++;
            blob.Metadata = new Dictionary<string, string>(metadata);
            blob.ETag = new ETag($"\"metadata-{_metadataCount}\"");
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobInfo(blob.ETag, default), TestResponse.Instance));
        }

        private Task<Response<bool>> DeleteIfExistsAsync(string name, BlobRequestConditions? conditions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add($"delete:{name}");
            DeleteCalls.Add(new(name, conditions?.IfMatch ?? default, conditions?.IfNoneMatch ?? default));
            BeforeDelete?.Invoke(name, conditions);
            if (!_blobs.TryGetValue(name, out _))
            {
                return Task.FromResult(Response.FromValue(false, TestResponse.Instance));
            }

            ThrowIfConditionsFail(name, conditions);
            _blobs.Remove(name);
            return Task.FromResult(Response.FromValue(true, TestResponse.Instance));
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

        private Task<Response<BlobProperties>> GetPropertiesAsync(string name, BlobRequestConditions? conditions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PropertiesCalls.Add(new(name, conditions?.IfMatch ?? default, conditions?.IfNoneMatch ?? default));
            if (!_blobs.TryGetValue(name, out var blob))
            {
                throw new RequestFailedException(404, "Blob does not exist.");
            }

            ThrowIfConditionsFail(name, conditions);

            var result = BlobsModelFactory.BlobProperties(
                contentLength: blob.Content.Length,
                contentType: blob.ContentType,
                eTag: blob.ETag,
                blobCommittedBlockCount: blob.CommittedBlockCount,
                blobCopyStatus: default(CopyStatus?),
                blobType: BlobType.Append,
                metadata: blob.Metadata,
                isSealed: blob.IsSealed,
                immutabilityPolicy: null,
                hasLegalHold: false);
            return Task.FromResult(Response.FromValue(result, TestResponse.Instance));
        }

        private void ThrowIfConditionsFail(string name, BlobRequestConditions? conditions)
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
        }

        private sealed class FakeAppendBlobClient(FakeAppendBlobStore store, string name) : AppendBlobClient
        {
            public override string BlobContainerName => "container";

            public override string Name => name;

            public override int AppendBlobMaxAppendBlockBytes => (int)AzureBlobJournalStorage.MaxAppendBlockBytes;

            public override int AppendBlobMaxBlocks => 50_000;

            public override Task<Response<BlobContentInfo>> CreateAsync(AppendBlobCreateOptions options, CancellationToken cancellationToken = default)
                => store.CreateAsync(name, options, cancellationToken);

            public override Task<Response<BlobAppendInfo>> AppendBlockAsync(Stream content, AppendBlobAppendBlockOptions options, CancellationToken cancellationToken = default)
                => store.AppendBlockAsync(name, content, options, cancellationToken);

            public override Task<Response<BlobInfo>> SealAsync(AppendBlobRequestConditions conditions = default!, CancellationToken cancellationToken = default)
                => store.SealAsync(name, conditions, cancellationToken);

            public override Task<Response<BlobInfo>> SetMetadataAsync(IDictionary<string, string> metadata, BlobRequestConditions conditions = default!, CancellationToken cancellationToken = default)
                => store.SetMetadataAsync(name, metadata, conditions, cancellationToken);

            public override Task<Response<bool>> DeleteIfExistsAsync(DeleteSnapshotsOption snapshotsOption = DeleteSnapshotsOption.None, BlobRequestConditions conditions = default!, CancellationToken cancellationToken = default)
                => store.DeleteIfExistsAsync(name, conditions, cancellationToken);

            public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(BlobDownloadOptions? options = null, CancellationToken cancellationToken = default)
                => store.DownloadStreamingAsync(name, options, cancellationToken);

            public override Task<Response<BlobProperties>> GetPropertiesAsync(BlobRequestConditions conditions = default!, CancellationToken cancellationToken = default)
                => store.GetPropertiesAsync(name, conditions, cancellationToken);
        }
    }

    private sealed class FakeBlockBlobStore
    {
        private readonly Dictionary<string, StoredBlockBlob> _blobs = [];
        private int _uploadCount;

        public bool FailNextUpload { get; set; }

        public List<BlockUploadCall> UploadCalls { get; } = [];

        public List<BlockDownloadCall> DownloadCalls { get; } = [];

        public List<BlockDeleteCall> DeleteCalls { get; } = [];

        public BlockBlobClient GetBlockBlobClient(string name) => new FakeBlockBlobClient(this, name);

        public void Add(string name, byte[] content, ETag eTag, IDictionary<string, string> metadata)
            => _blobs[name] = new StoredBlockBlob(content, eTag, new Dictionary<string, string>(metadata), ContentType: null);

        public bool Exists(string name) => _blobs.ContainsKey(name);

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

        private Task<Response<bool>> DeleteIfExistsAsync(string name, BlobRequestConditions? conditions, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCalls.Add(new(name, conditions?.IfMatch ?? default, conditions?.IfNoneMatch ?? default));

            if (!_blobs.TryGetValue(name, out var blob))
            {
                return Task.FromResult(Response.FromValue(false, TestResponse.Instance));
            }

            if (conditions?.IfMatch is { } ifMatch && ifMatch != default && ifMatch != blob.ETag)
            {
                throw new RequestFailedException(412, "Checkpoint ETag mismatch.");
            }

            _blobs.Remove(name);
            return Task.FromResult(Response.FromValue(true, TestResponse.Instance));
        }

        private sealed class FakeBlockBlobClient(FakeBlockBlobStore store, string name) : BlockBlobClient
        {
            public override string BlobContainerName => "container";

            public override string Name => name;

            public override Task<Response<BlobContentInfo>> UploadAsync(Stream content, BlobUploadOptions options, CancellationToken cancellationToken = default)
                => store.UploadAsync(name, content, options, cancellationToken);

            public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(BlobDownloadOptions? options = null, CancellationToken cancellationToken = default)
                => store.DownloadStreamingAsync(name, options, cancellationToken);

            public override Task<Response<bool>> DeleteIfExistsAsync(DeleteSnapshotsOption snapshotsOption = DeleteSnapshotsOption.None, BlobRequestConditions conditions = default!, CancellationToken cancellationToken = default)
                => store.DeleteIfExistsAsync(name, conditions, cancellationToken);
        }
    }

    private sealed record CreateCall(string Name, ETag IfMatch, ETag IfNoneMatch, string? ContentType, IDictionary<string, string> Metadata);

    private sealed record AppendCall(string Name, ETag IfMatch, ETag IfNoneMatch, byte[] Payload);

    private sealed record SealCall(string Name, ETag IfMatch);

    private sealed record SetMetadataCall(string Name, ETag IfMatch, ETag IfNoneMatch, IDictionary<string, string> Metadata);

    private sealed record DeleteCall(string Name, ETag IfMatch, ETag IfNoneMatch);

    private sealed record DownloadCall(string Name);

    private sealed record GetPropertiesCall(string Name, ETag IfMatch, ETag IfNoneMatch);

    private sealed record BlockUploadCall(string Name, ETag IfMatch, ETag IfNoneMatch, string? ContentType, IDictionary<string, string> Metadata, byte[] Payload);

    private sealed record BlockDownloadCall(string Name, ETag IfMatch);

    private sealed record BlockDeleteCall(string Name, ETag IfMatch, ETag IfNoneMatch);

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
