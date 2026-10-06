using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Data.Tables.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using TestExtensions;
using Xunit;

namespace Tester.AzureUtils.Persistence;

[TestCategory("Persistence"), TestCategory("AzureStorage"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("AzureStorage")]
[TestArea("Persistence")]
public sealed class AzureTableGrainStorageCancellationTests
{
    private const string GrainType = "cancellation-grain";
    private const string TableName = "CancellationTests";
    private const string PartitionKey = "cancellation-service_cancellation-grain_grain-1";
    private const string OriginalETag = "\"original-etag\"";
    private const string UpdatedETag = "sdk-etag";
    private const string UpdatedETagHeader = "\"sdk-etag\"";
    private static readonly GrainId TestGrainId = GrainId.Create(GrainType, "grain-1");

    public static IEnumerable<object?[]> SdkCases
    {
        get
        {
            yield return [Branch.Read, OriginalETag];
            yield return [Branch.WriteCreate, null];
            yield return [Branch.WriteCreate, ""];
            yield return [Branch.WriteReplace, OriginalETag];
            yield return [Branch.ClearOverwriteCreate, null];
            yield return [Branch.ClearOverwriteCreate, ""];
            yield return [Branch.ClearOverwriteReplace, OriginalETag];
            yield return [Branch.ClearDelete, OriginalETag];
        }
    }

    public static IEnumerable<object?[]> InFlightCases
        => WithRecordExistsVariations(SdkCases);

    public static IEnumerable<object?[]> PreCanceledCases
        => WithRecordExistsVariations(SdkCases.Concat(
        [
            new object?[] { Branch.ClearDelete, null },
            new object?[] { Branch.ClearDelete, "" },
            new object?[] { Branch.ClearDelete, " " },
        ]));

    private static IEnumerable<object?[]> WithRecordExistsVariations(IEnumerable<object?[]> cases)
    {
        foreach (var testCase in cases)
        {
            yield return [testCase[0], testCase[1], false];
            yield return [testCase[0], testCase[1], true];
        }
    }

    [Theory]
    [MemberData(nameof(SdkCases))]
    public async Task Operation_ForwardsExactCallerTokenToSdk(Branch branch, string? etag)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = await Fixture.CreateAsync(branch);
        var state = CreateState(etag, IsClear(branch));
        var original = Assert.IsType<Dictionary<string, int>>(state.State);
        fixture.Table.Expect(branch, etag, cancellation.Token);
        fixture.Table.Completion.SetResult(new SdkResponse());

        await Invoke(fixture.Storage, branch, state, cancellation.Token);

        AssertSuccessfulOperation(fixture, branch, state, original);
    }

    [Theory]
    [MemberData(nameof(SdkCases))]
    public async Task LegacyOperation_ForwardsNoneToSdk(Branch branch, string? etag)
    {
        var fixture = await Fixture.CreateAsync(branch);
        var state = CreateState(etag, IsClear(branch));
        var original = Assert.IsType<Dictionary<string, int>>(state.State);
        fixture.Table.Expect(branch, etag, CancellationToken.None);
        fixture.Table.Completion.SetResult(new SdkResponse());

        await Invoke(fixture.Storage, branch, state, CancellationToken.None, legacy: true);

        AssertSuccessfulOperation(fixture, branch, state, original);
    }

    [Theory]
    [MemberData(nameof(PreCanceledCases))]
    public async Task PreCanceledOperation_DoesNotCallSdkOrChangeGrainState(
        Branch branch, string? etag, bool recordExists)
    {
        var fixture = await Fixture.CreateAsync(branch);
        var state = CreateState(etag, recordExists);
        var original = Assert.IsType<Dictionary<string, int>>(state.State);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // No SDK operation is configured: even a call with the right token is unexpected.

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Invoke(fixture.Storage, branch, state, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, fixture.Table.OperationCalls);
        AssertUnchanged(state, original, etag, recordExists);
        Assert.Equal(0, fixture.Serializer.SerializeCalls);
        Assert.Equal(0, fixture.Serializer.DeserializeCalls);
        Assert.Equal(0, fixture.Activators.Calls);
        Assert.Empty(fixture.Logger.StorageErrors);
    }

    [Theory]
    [MemberData(nameof(InFlightCases))]
    public async Task InFlightCancellation_PropagatesOriginalExceptionWithoutChangingStateOrLoggingErrors(
        Branch branch, string? etag, bool recordExists)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = await Fixture.CreateAsync(branch);
        var state = CreateState(etag, recordExists);
        var original = Assert.IsType<Dictionary<string, int>>(state.State);
        fixture.Table.Expect(branch, etag, cancellation.Token);
        var failure = new OperationCanceledException("Controlled SDK cancellation.", cancellation.Token);

        var operation = Invoke(fixture.Storage, branch, state, cancellation.Token);
        try
        {
            // Race the entry barrier with the operation so a broken/omitted SDK call fails
            // immediately instead of waiting forever for a barrier which will never arrive.
            Assert.Same(fixture.Table.Entered.Task, await Task.WhenAny(fixture.Table.Entered.Task, operation));
            await fixture.Table.Entered.Task;
            Assert.False(operation.IsCompleted);
            Assert.False(cancellation.IsCancellationRequested);
            AssertUnchanged(state, original, etag, recordExists);

            cancellation.Cancel();
            fixture.Table.Completion.SetException(failure);
            var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => operation);

            Assert.Same(failure, exception);
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Equal(1, fixture.Table.OperationCalls);
            AssertUnchanged(state, original, etag, recordExists);
            Assert.Equal(IsWrite(branch) ? 1 : 0, fixture.Serializer.SerializeCalls);
            Assert.Equal(0, fixture.Serializer.DeserializeCalls);
            Assert.Equal(0, fixture.Activators.Calls);
            // Logging is enabled, and the same logger observes provider AND manager logs.
            Assert.Empty(fixture.Logger.StorageErrors);
        }
        finally
        {
            fixture.Table.Completion.TrySetCanceled(cancellation.Token);
        }
    }

    [Theory]
    [MemberData(nameof(SdkCases))]
    public async Task SdkCancellation_WithoutCallerCancellation_PreservesStateAndErrorLogging(Branch branch, string? etag)
    {
        using var cancellation = new CancellationTokenSource();
        using var sdkCancellation = new CancellationTokenSource();
        sdkCancellation.Cancel();
        var fixture = await Fixture.CreateAsync(branch);
        var state = CreateState(etag, recordExists: true);
        var original = Assert.IsType<Dictionary<string, int>>(state.State);
        var failure = new OperationCanceledException("SDK cancellation.", sdkCancellation.Token);
        fixture.Table.Expect(branch, etag, cancellation.Token);
        fixture.Table.Completion.SetException(failure);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => Invoke(fixture.Storage, branch, state, cancellation.Token));

        Assert.Same(failure, exception);
        Assert.Equal(sdkCancellation.Token, exception.CancellationToken);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(1, fixture.Table.OperationCalls);
        AssertUnchanged(state, original, etag, recordExists: true);
        Assert.Equal(IsWrite(branch) ? 1 : 0, fixture.Serializer.SerializeCalls);
        Assert.Equal(0, fixture.Serializer.DeserializeCalls);
        Assert.Equal(0, fixture.Activators.Calls);
        Assert.Equal(branch == Branch.Read ? 0 : 2, fixture.Logger.StorageErrors.Count);
        Assert.All(fixture.Logger.StorageErrors, entry => Assert.Same(failure, entry.Exception));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ClearDelete_WithoutETag_DoesNotCallSdk(string? etag)
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = await Fixture.CreateAsync(Branch.ClearDelete);
        var state = CreateState(etag, recordExists: true);
        var original = Assert.IsType<Dictionary<string, int>>(state.State);

        await fixture.Storage.ClearStateAsync(GrainType, TestGrainId, state, cancellation.Token);

        Assert.Equal(0, fixture.Table.OperationCalls);
        Assert.NotSame(original, state.State);
        Assert.Empty(Assert.IsType<Dictionary<string, int>>(state.State));
        Assert.Null(state.ETag);
        Assert.False(state.RecordExists);
        Assert.Equal(1, fixture.Activators.Calls);
        Assert.Equal(0, fixture.Serializer.SerializeCalls);
        Assert.Equal(0, fixture.Serializer.DeserializeCalls);
        Assert.Empty(fixture.Logger.StorageErrors);
    }

    private static GrainState<Dictionary<string, int>> CreateState(string? etag, bool recordExists)
        => new(new Dictionary<string, int> { ["value"] = 42 })
        {
            ETag = etag,
            RecordExists = recordExists,
        };

    private static Task Invoke(
        IGrainStorage storage,
        Branch branch,
        IGrainState<Dictionary<string, int>> state,
        CancellationToken token,
        bool legacy = false)
        => branch switch
        {
            Branch.Read => legacy
                ? storage.ReadStateAsync(GrainType, TestGrainId, state)
                : storage.ReadStateAsync(GrainType, TestGrainId, state, token),
            Branch.WriteCreate or Branch.WriteReplace => legacy
                ? storage.WriteStateAsync(GrainType, TestGrainId, state)
                : storage.WriteStateAsync(GrainType, TestGrainId, state, token),
            Branch.ClearOverwriteCreate or Branch.ClearOverwriteReplace or Branch.ClearDelete => legacy
                ? storage.ClearStateAsync(GrainType, TestGrainId, state)
                : storage.ClearStateAsync(GrainType, TestGrainId, state, token),
            _ => throw new ArgumentOutOfRangeException(nameof(branch)),
        };

    private static bool IsWrite(Branch branch) => branch is Branch.WriteCreate or Branch.WriteReplace;

    private static bool IsClear(Branch branch)
        => branch is Branch.ClearOverwriteCreate or Branch.ClearOverwriteReplace or Branch.ClearDelete;

    private static void AssertUnchanged(
        IGrainState<Dictionary<string, int>> state,
        Dictionary<string, int> original,
        string? etag,
        bool recordExists)
    {
        var current = Assert.IsType<Dictionary<string, int>>(state.State);
        Assert.Same(original, current);
        Assert.Equal(42, current["value"]);
        Assert.Single(current);
        Assert.Equal(etag, state.ETag);
        Assert.Equal(recordExists, state.RecordExists);
    }

    private static void AssertSuccessfulOperation(
        Fixture fixture, Branch branch, IGrainState<Dictionary<string, int>> state, Dictionary<string, int> original)
    {
        Assert.Equal(1, fixture.Table.OperationCalls);
        Assert.Equal(branch == Branch.ClearDelete ? null : UpdatedETag, state.ETag);
        Assert.Equal(!IsClear(branch), state.RecordExists);
        var current = Assert.IsType<Dictionary<string, int>>(state.State);
        if (IsClear(branch))
        {
            Assert.NotSame(original, current);
            Assert.Empty(current);
        }
        else if (branch == Branch.Read)
        {
            Assert.NotSame(original, current);
            Assert.Equal(73, current["value"]);
            Assert.Single(current);
        }
        else
        {
            Assert.Same(original, current);
            Assert.Equal(42, current["value"]);
            Assert.Single(current);
        }

        Assert.Equal(IsWrite(branch) ? 1 : 0, fixture.Serializer.SerializeCalls);
        Assert.Equal(branch == Branch.Read ? 1 : 0, fixture.Serializer.DeserializeCalls);
        Assert.Equal(IsClear(branch) ? 1 : 0, fixture.Activators.Calls);
        Assert.Empty(fixture.Logger.StorageErrors);
    }

    public enum Branch
    {
        Read,
        WriteCreate,
        WriteReplace,
        ClearOverwriteCreate,
        ClearOverwriteReplace,
        ClearDelete,
    }

    private sealed class Fixture
    {
        public StrictTableClient Table { get; } = new();
        public CountingSerializer Serializer { get; } = new();
        public CountingActivators Activators { get; } = new();
        public RecordingLogger Logger { get; } = new();
        public IGrainStorage Storage { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(Branch branch)
        {
            var fixture = new Fixture();
            var service = new StrictTableServiceClient(fixture.Table);
            var options = new AzureTableStorageOptions
            {
                TableName = TableName,
                TableServiceClient = service,
                DeleteStateOnClear = branch == Branch.ClearDelete,
                GrainStorageSerializer = fixture.Serializer,
            };
            // Slow-access diagnostics are unrelated to cancellation, even on paused/debugged runs.
            options.StoragePolicyOptions.OperationTimeout = TimeSpan.MaxValue;
            var storage = new AzureTableGrainStorage(
                "CancellationStorage",
                options,
                Options.Create(new ClusterOptions { ServiceId = "cancellation-service", ClusterId = "unit-cluster" }),
                fixture.Logger,
                fixture.Activators);
            var lifecycle = new SiloLifecycleSubject(NullLogger<SiloLifecycleSubject>.Instance);
            storage.Participate(lifecycle);
            await lifecycle.OnStart(CancellationToken.None);
            fixture.Storage = storage;

            Assert.Equal(1, service.GetTableCalls);
            Assert.Equal(1, fixture.Table.InitCalls);
            Assert.Equal(0, fixture.Table.OperationCalls);
            Assert.Empty(fixture.Logger.StorageErrors);
            return fixture;
        }
    }

    // Hand-written strict SDK mocks: no real client pipeline, extra packages or reflection.
    private sealed class StrictTableServiceClient(StrictTableClient table) : TableServiceClient
    {
        public int GetTableCalls { get; private set; }

        public override TableClient GetTableClient(string tableName)
        {
            Assert.Equal(TableName, tableName);
            Assert.Equal(1, ++GetTableCalls);
            return table;
        }
    }

    private sealed class StrictTableClient : TableClient
    {
        private Branch? _expectedBranch;
        private string? _expectedETag;
        private CancellationToken _expectedToken;
        public int InitCalls { get; private set; }
        public int OperationCalls { get; private set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Response> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Expect(Branch branch, string? etag, CancellationToken token)
        {
            _expectedBranch = branch;
            _expectedETag = etag;
            _expectedToken = token;
        }

        public override Task<Response<TableItem>> CreateIfNotExistsAsync(CancellationToken cancellationToken = default)
        {
            Assert.Equal(CancellationToken.None, cancellationToken);
            Assert.Equal(1, ++InitCalls);
            return Task.FromResult(Response.FromValue(
                TableModelFactory.TableItem(AzureTableGrainStorageCancellationTests.TableName), new SdkResponse()));
        }

        public override async Task<NullableResponse<T>> GetEntityIfExistsAsync<T>(
            string partitionKey, string rowKey, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
        {
            Assert.Null(select);
            var response = await Dispatch(Branch.Read, partitionKey, rowKey, cancellationToken);
            var entity = new TableEntity(partitionKey, rowKey)
            {
                ETag = new ETag(UpdatedETag),
                ["Data"] = new BinaryData("{\"value\":73}").ToArray(),
            };
            return Response.FromValue((T)(object)entity, response);
        }

        public override Task<Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default)
        {
            var data = Assert.IsType<TableEntity>(entity);
            Assert.True(_expectedBranch is Branch.WriteCreate or Branch.ClearOverwriteCreate);
            Assert.Equal(_expectedETag ?? string.Empty, data.ETag.ToString() ?? string.Empty);
            AssertPayload(data);
            return Dispatch(_expectedBranch.Value, data.PartitionKey, data.RowKey, cancellationToken);
        }

        public override Task<Response> UpdateEntityAsync<T>(
            T entity, ETag ifMatch, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default)
        {
            var data = Assert.IsType<TableEntity>(entity);
            Assert.True(_expectedBranch is Branch.WriteReplace or Branch.ClearOverwriteReplace);
            Assert.Equal(_expectedETag, ifMatch.ToString());
            Assert.Equal(ifMatch, data.ETag);
            Assert.Equal(TableUpdateMode.Replace, mode);
            AssertPayload(data);
            return Dispatch(_expectedBranch.Value, data.PartitionKey, data.RowKey, cancellationToken);
        }

        public override Task<Response> DeleteEntityAsync(
            string partitionKey, string rowKey, ETag ifMatch = default, CancellationToken cancellationToken = default)
        {
            Assert.Equal(_expectedETag, ifMatch.ToString());
            return Dispatch(Branch.ClearDelete, partitionKey, rowKey, cancellationToken);
        }

        private void AssertPayload(TableEntity data)
        {
            Assert.False(data.ContainsKey("StringData"));
            if (_expectedBranch is Branch.WriteCreate or Branch.WriteReplace)
            {
                Assert.Equal(new BinaryData("{\"value\":42}").ToArray(), Assert.IsType<byte[]>(data["Data"]));
            }
            else
            {
                Assert.False(data.ContainsKey("Data"));
            }
        }

        private Task<Response> Dispatch(Branch branch, string partitionKey, string rowKey, CancellationToken token)
        {
            Assert.Equal(1, ++OperationCalls);
            Assert.Equal(_expectedBranch, (Branch?)branch);
            Assert.Equal(PartitionKey, partitionKey);
            Assert.Equal(GrainType, rowKey);
            Assert.Equal(_expectedToken, token);
            Entered.SetResult(true);
            return Completion.Task;
        }
    }

    private sealed class CountingSerializer : IGrainStorageSerializer
    {
        public int SerializeCalls { get; private set; }
        public int DeserializeCalls { get; private set; }

        public BinaryData Serialize<T>(T? input)
        {
            SerializeCalls++;
            return BinaryData.FromObjectAsJson(input);
        }

        public T? Deserialize<T>(BinaryData input)
        {
            DeserializeCalls++;
            return input.ToObjectFromJson<T>();
        }
    }

    private sealed class CountingActivators : IActivatorProvider, IActivator<Dictionary<string, int>>
    {
        public int Calls { get; private set; }

        public IActivator<T> GetActivator<T>()
        {
            Calls++;
            Assert.Equal(typeof(Dictionary<string, int>), typeof(T));
            return (IActivator<T>)(object)this;
        }

        public Dictionary<string, int> Create() => new();
    }

    private sealed class RecordingLogger : ILogger<AzureTableGrainStorage>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> StorageErrors { get; } = new();
        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                StorageErrors.Add((logLevel, exception, formatter(state, exception)));
            }
        }
    }

    private sealed class SdkResponse : Response
    {
        public override int Status => 204;
        public override string ReasonPhrase => "No Content";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "unit-test";
        public override void Dispose() { }

        protected override bool ContainsHeader(string name) => name.Equals("ETag", StringComparison.OrdinalIgnoreCase);

        protected override bool TryGetHeader(string name, out string value)
        {
            value = ContainsHeader(name) ? UpdatedETagHeader : null!;
            return ContainsHeader(name);
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            values = ContainsHeader(name) ? [UpdatedETagHeader] : [];
            return ContainsHeader(name);
        }

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [new("ETag", UpdatedETagHeader)];
    }
}
