using System.Buffers;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class AzureAppendBlobJournalStorageTests
{
    [Fact]
    public async Task DeleteAsync_AllowsNextAppendToRecreateBlob()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureAppendBlobJournalStorage(client, NullLogger<AzureAppendBlobJournalStorage>.Instance, static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot));

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
        var storage = new AzureAppendBlobJournalStorage(client, "application/jsonl", NullLogger<AzureAppendBlobJournalStorage>.Instance, static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot));

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        Assert.Equal("application/jsonl", client.CreateCalls.Single().ContentType);
    }

    [Fact]
    public async Task AppendAsync_WhenMimeTypeUnavailable_LeavesBlobContentTypeUnset()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureAppendBlobJournalStorage(client, mimeType: null, NullLogger<AzureAppendBlobJournalStorage>.Instance, static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot));

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        Assert.Null(client.CreateCalls.Single().ContentType);
    }

    [Fact]
    public void GetBlobNameWithExtension_AppendsSelectedJournalFormatExtension()
    {
        var options = new AzureAppendBlobJournalStorageOptions
        {
            GetBlobName = _ => "journals/test-grain"
        };

        var blobName = options.GetBlobNameWithExtension(GrainId.Create("test-grain", "0"), new TestJournalFormat(".jsonl"));

        Assert.Equal("journals/test-grain.jsonl", blobName);
    }

    [Fact]
    public void GetBlobNameWithExtension_DoesNotDuplicateSelectedJournalFormatExtension()
    {
        var options = new AzureAppendBlobJournalStorageOptions
        {
            GetBlobName = _ => "journals/test-grain.jsonl"
        };

        var blobName = options.GetBlobNameWithExtension(GrainId.Create("test-grain", "0"), new TestJournalFormat(".jsonl"));

        Assert.Equal("journals/test-grain.jsonl", blobName);
    }

    [Fact]
    public async Task ReadAsync_RestoresEmptyBlobFromSnapshotUsingIfMatchCondition()
    {
        var emptyBlobETag = new ETag("\"empty\"");
        var client = new FakeAppendBlobClient();
        client.Downloads.Enqueue(new DownloadResult([], emptyBlobETag, new Dictionary<string, string> { ["snapshot"] = "snapshot-1" }));
        client.Downloads.Enqueue(new DownloadResult([1, 2, 3], new ETag("\"restored\""), new Dictionary<string, string>()));
        var storage = new AzureAppendBlobJournalStorage(client, NullLogger<AzureAppendBlobJournalStorage>.Instance, static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot));

        await storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None);

        Assert.Equal(1, client.CopyCallCount);
        Assert.NotNull(client.LastCopyOptions);
        Assert.Equal(emptyBlobETag, client.LastCopyOptions.DestinationConditions.IfMatch);
        Assert.Equal(default, client.LastCopyOptions.DestinationConditions.IfNoneMatch);
        Assert.Equal("snapshot-1", client.LastSnapshot);
    }

    [Fact]
    public async Task ReplaceAsync_CreatesRecoverableSnapshotAndDeletesItAfterAppend()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureAppendBlobJournalStorage(client, "application/jsonl", NullLogger<AzureAppendBlobJournalStorage>.Instance, static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot));

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2, 3]), CancellationToken.None);

        Assert.Equal(2, client.CreateCallCount);
        Assert.Equal(2, client.AppendCallCount);
        Assert.Equal(1, client.CreateSnapshotCallCount);
        Assert.Equal(1, client.SnapshotDeleteCallCount);
        Assert.Equal("snapshot-1", client.LastSnapshot);
        Assert.NotNull(client.LastSnapshotConditions);
        Assert.Equal(new ETag("\"append-1\""), client.LastSnapshotConditions.IfMatch);

        var replaceCreate = client.CreateCalls[1];
        Assert.Equal(new ETag("\"append-1\""), replaceCreate.IfMatch);
        Assert.Equal(default, replaceCreate.IfNoneMatch);
        Assert.Equal("snapshot-1", replaceCreate.Metadata["snapshot"]);
        Assert.Equal("application/jsonl", replaceCreate.ContentType);

        var replaceAppend = client.AppendCalls[1];
        Assert.Equal(new ETag("\"create-2\""), replaceAppend.IfMatch);
        Assert.Equal([2, 3], replaceAppend.Payload);
        Assert.Null(client.LastSnapshotDeleteConditions);
    }

    [Fact]
    public async Task ReadAsync_WhenSnapshotCopyFails_SurfacesCopyFailure()
    {
        var client = new FakeAppendBlobClient { CopyStatus = CopyStatus.Failed };
        client.Downloads.Enqueue(new DownloadResult([], new ETag("\"empty\""), new Dictionary<string, string> { ["snapshot"] = "snapshot-1" }));
        var storage = new AzureAppendBlobJournalStorage(client, NullLogger<AzureAppendBlobJournalStorage>.Instance, static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None).AsTask());

        Assert.Contains("Copy did not complete successfully", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, client.CopyCallCount);
        Assert.Equal("snapshot-1", client.LastSnapshot);
    }

    [Fact]
    public async Task ReadAsync_UpdatesCompactionRequestFromCommittedBlockCount()
    {
        var client = new FakeAppendBlobClient();
        client.Downloads.Enqueue(new DownloadResult([1], new ETag("\"read\""), new Dictionary<string, string>(), BlobCommittedBlockCount: 11));
        var storage = new AzureAppendBlobJournalStorage(client, NullLogger<AzureAppendBlobJournalStorage>.Instance, static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot));

        await storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None);

        Assert.True(storage.IsCompactionRequested);

        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        Assert.False(storage.IsCompactionRequested);
    }

    [Fact]
    public async Task ReadAsync_DeletesOrphanSnapshotsLeftByInterruptedReplaceCalls()
    {
        var client = new FakeAppendBlobClient();
        client.Downloads.Enqueue(new DownloadResult([1, 2, 3], new ETag("\"read\""), new Dictionary<string, string>(), BlobCommittedBlockCount: 1));

        // Simulate a storage account that has accumulated orphan snapshots from
        // earlier ReplaceAsync calls that crashed before snapshot deletion.
        static async IAsyncEnumerable<string> EnumerateOrphans(AppendBlobClient _, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return "orphan-1";
            yield return "orphan-2";
            yield return "orphan-3";
        }

        var storage = new AzureAppendBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureAppendBlobJournalStorage>.Instance,
            static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot),
            EnumerateOrphans);

        await storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None);

        Assert.Equal(3, client.SnapshotDeleteCallCount);
    }

    [Fact]
    public async Task ReadAsync_WhenSnapshotEnumerationFails_StillCompletesSuccessfully()
    {
        var client = new FakeAppendBlobClient();
        client.Downloads.Enqueue(new DownloadResult([1], new ETag("\"read\""), new Dictionary<string, string>(), BlobCommittedBlockCount: 1));

        static async IAsyncEnumerable<string> ThrowingEnumerator(AppendBlobClient _, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("listing failed");
#pragma warning disable CS0162 // Unreachable code detected
            yield break;
#pragma warning restore CS0162
        }

        var storage = new AzureAppendBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureAppendBlobJournalStorage>.Instance,
            static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot),
            ThrowingEnumerator);

        // Should not throw; cleanup failure must not break recovery.
        await storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None);

        Assert.Equal(0, client.SnapshotDeleteCallCount);
    }

    [Fact]
    public async Task AppendAsync_WhenJournalFormatKeyConfigured_StampsBlobMetadata()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureAppendBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureAppendBlobJournalStorage>.Instance,
            static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot),
            snapshotEnumerator: null,
            journalFormatKey: "json-lines");

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);

        var create = client.CreateCalls.Single();
        Assert.True(create.Metadata.TryGetValue(AzureAppendBlobJournalStorage.FormatKeyMetadataKey, out var stamped));
        Assert.Equal("json-lines", stamped);
    }

    [Fact]
    public async Task ReplaceAsync_WhenJournalFormatKeyConfigured_PreservesFormatKeyMetadata()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureAppendBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureAppendBlobJournalStorage>.Instance,
            static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot),
            snapshotEnumerator: null,
            journalFormatKey: "json-lines");

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        await storage.ReplaceAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None);

        var replaceCreate = client.CreateCalls[1];
        Assert.Equal("snapshot-1", replaceCreate.Metadata["snapshot"]);
        Assert.Equal("json-lines", replaceCreate.Metadata[AzureAppendBlobJournalStorage.FormatKeyMetadataKey]);
    }

    [Fact]
    public async Task ReadAsync_WhenStoredFormatKeyDiffersFromConfigured_ThrowsExplanatoryError()
    {
        var client = new FakeAppendBlobClient();
        client.Downloads.Enqueue(new DownloadResult(
            [1, 2, 3],
            new ETag("\"read\""),
            new Dictionary<string, string> { [AzureAppendBlobJournalStorage.FormatKeyMetadataKey] = "orleans-binary" },
            BlobCommittedBlockCount: 1));
        var storage = new AzureAppendBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureAppendBlobJournalStorage>.Instance,
            static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot),
            snapshotEnumerator: null,
            journalFormatKey: "json-lines");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None).AsTask());

        Assert.Contains("orleans-binary", exception.Message, StringComparison.Ordinal);
        Assert.Contains("json-lines", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_WhenStoredFormatKeyMatchesConfigured_Succeeds()
    {
        var client = new FakeAppendBlobClient();
        client.Downloads.Enqueue(new DownloadResult(
            [1, 2, 3],
            new ETag("\"read\""),
            new Dictionary<string, string> { [AzureAppendBlobJournalStorage.FormatKeyMetadataKey] = "json-lines" },
            BlobCommittedBlockCount: 1));
        var storage = new AzureAppendBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureAppendBlobJournalStorage>.Instance,
            static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot),
            snapshotEnumerator: null,
            journalFormatKey: "json-lines");

        await storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None);
    }

    [Fact]
    public async Task ReadAsync_WhenLegacyBlobHasNoFormatKeyMetadata_AllowsReadForBackCompat()
    {
        var client = new FakeAppendBlobClient();
        client.Downloads.Enqueue(new DownloadResult([1, 2, 3], new ETag("\"read\""), new Dictionary<string, string>(), BlobCommittedBlockCount: 1));
        var storage = new AzureAppendBlobJournalStorage(
            client,
            mimeType: null,
            NullLogger<AzureAppendBlobJournalStorage>.Instance,
            static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot),
            snapshotEnumerator: null,
            journalFormatKey: "json-lines");

        // Should not throw — legacy data without the metadata stamp is accepted.
        await storage.ReadAsync(DiscardingJournalStorageConsumer.Instance, CancellationToken.None);
    }

    [Fact]
    public async Task AppendAsync_WhenAppendFails_DoesNotAdvanceAppendCondition()
    {
        var client = new FakeAppendBlobClient();
        var storage = new AzureAppendBlobJournalStorage(client, NullLogger<AzureAppendBlobJournalStorage>.Instance, static (client, snapshot) => ((FakeAppendBlobClient)client).CreateSnapshotClient(snapshot));

        await storage.AppendAsync(new ReadOnlySequence<byte>([1]), CancellationToken.None);
        client.FailNextAppend = true;

        await Assert.ThrowsAsync<RequestFailedException>(
            () => storage.AppendAsync(new ReadOnlySequence<byte>([2]), CancellationToken.None).AsTask());
        await storage.AppendAsync(new ReadOnlySequence<byte>([3]), CancellationToken.None);

        Assert.Equal(new ETag("\"append-1\""), client.AppendCalls[1].IfMatch);
        Assert.Equal(new ETag("\"append-1\""), client.AppendCalls[2].IfMatch);
        Assert.Equal([3], client.AppendCalls[2].Payload);
    }

    private sealed class DiscardingJournalStorageConsumer : IJournalStorageConsumer
    {
        public static DiscardingJournalStorageConsumer Instance { get; } = new();

        public void Consume(JournalReadBuffer buffer) => buffer.Skip(buffer.Length);
    }

    private sealed class FakeAppendBlobClient : AppendBlobClient
    {
        private readonly FakeAppendBlobClient _root;
        private readonly string? _snapshot;
        private bool _exists;
        private ETag _eTag = new("\"initial\"");
        private int _successfulAppendCount;

        public FakeAppendBlobClient()
        {
            _root = this;
        }

        private FakeAppendBlobClient(FakeAppendBlobClient root, string snapshot)
        {
            _root = root;
            _snapshot = snapshot;
        }

        public override string BlobContainerName => "container";

        public override string Name => "blob";

        public int CreateCallCount => CreateCalls.Count;

        public int AppendCallCount => AppendCalls.Count;

        public int CopyCallCount { get; private set; }

        public int CreateSnapshotCallCount { get; private set; }

        public int SnapshotDeleteCallCount { get; private set; }

        public bool FailNextAppend { get; set; }

        public CopyStatus CopyStatus { get; set; } = CopyStatus.Success;

        public string? LastSnapshot { get; private set; }

        public BlobRequestConditions? LastSnapshotConditions { get; private set; }

        public BlobRequestConditions? LastSnapshotDeleteConditions { get; private set; }

        public BlobCopyFromUriOptions? LastCopyOptions { get; private set; }

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
            return Response.FromValue(
                BlobsModelFactory.BlobAppendInfo(_root._eTag, default, null, null, "0", _root._successfulAppendCount, false, null, null),
                TestResponse.Instance);
        }

        public override Task<Response> DeleteAsync(DeleteSnapshotsOption snapshotsOption = DeleteSnapshotsOption.None, BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_snapshot is null)
            {
                _root._exists = false;
            }
            else
            {
                _root.SnapshotDeleteCallCount++;
                _root.LastSnapshotDeleteConditions = conditions;
            }

            return Task.FromResult<Response>(TestResponse.Instance);
        }

        public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(BlobDownloadOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var download = _root.Downloads.Dequeue();
            var details = BlobsModelFactory.BlobDownloadDetails(
                blobType: BlobType.Append,
                contentLength: download.Content.Length,
                metadata: download.Metadata,
                blobCommittedBlockCount: download.BlobCommittedBlockCount,
                eTag: download.ETag);
            var result = BlobsModelFactory.BlobDownloadStreamingResult(new MemoryStream(download.Content), details);
            return Task.FromResult(Response.FromValue(result, TestResponse.Instance));
        }

        public override Task<Response<BlobCopyInfo>> SyncCopyFromUriAsync(Uri source, BlobCopyFromUriOptions options, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _root.CopyCallCount++;
            _root.LastCopyOptions = options;
            if (_root.CopyStatus is CopyStatus.Success)
            {
                _root._eTag = new ETag($"\"copy-{_root.CopyCallCount}\"");
            }

            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobCopyInfo(_root._eTag, default, null, "copy", _root.CopyStatus),
                TestResponse.Instance));
        }

        public override Task<Response<BlobSnapshotInfo>> CreateSnapshotAsync(IDictionary<string, string>? metadata = null, BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _root.CreateSnapshotCallCount++;
            _root.LastSnapshotConditions = conditions is null
                ? null
                : new BlobRequestConditions { IfMatch = conditions.IfMatch, IfNoneMatch = conditions.IfNoneMatch };
            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobSnapshotInfo($"snapshot-{_root.CreateSnapshotCallCount}", _root._eTag, default, null, false),
                TestResponse.Instance));
        }

        public AppendBlobClient CreateSnapshotClient(string snapshot)
        {
            _root.LastSnapshot = snapshot;
            return new FakeAppendBlobClient(_root, snapshot);
        }

        public override Uri GenerateSasUri(BlobSasPermissions permissions, DateTimeOffset expiresOn)
            => new($"https://example.com/{Name}?snapshot={_snapshot}");
    }

    private sealed record DownloadResult(
        byte[] Content,
        ETag ETag,
        IDictionary<string, string> Metadata,
        int BlobCommittedBlockCount = 0);

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

    private sealed class TestJournalFormat(string fileExtension) : IJournalFormat
    {
        public string FileExtension => fileExtension;

        public string? MimeType => null;

        public IJournalBatchWriter CreateWriter() => throw new NotSupportedException();

        public void Read(JournalReadBuffer input, IStateResolver resolver) => throw new NotSupportedException();
    }
}
