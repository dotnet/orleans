using System.Data;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Persistence.AdoNet.Storage;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using UnitTests.StorageTests.Relational.Fakes;

namespace UnitTests.StorageTests.Relational;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Persistence")]
public sealed class AdoNetGrainStorageCancellationTests
{
    private const string GrainType = "cancellation-state";
    private static readonly GrainId TestGrainId = GrainId.Create("cancellation-grain", "key");

    [Theory]
    [InlineData("Read")]
    [InlineData("Write")]
    [InlineData("Clear")]
    [InlineData("Delete")]
    public async Task Operations_ForwardExactTokenAndUpdateState(string operation)
    {
        var relationalStorage = ExpectSuccess(operation);
        var fixture = new StorageFixture(relationalStorage, operation == "Delete");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await InvokeAsync(fixture.Storage, operation, fixture.State, cancellation.Token);

        var call = Assert.Single(relationalStorage.Calls);
        Assert.Equal(Query(operation), call.Query);
        Assert.Equal(cancellation.Token, call.CancellationToken);
        AssertSuccessfulState(fixture, operation);
        if (operation == "Write")
        {
            Assert.Equal(
                StorageFixture.Payload,
                Assert.IsType<byte[]>(((IDataParameter)call.Command.Parameters["PayloadBinary"]).Value));
        }

        relationalStorage.VerifyComplete();
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Write")]
    [InlineData("Clear")]
    [InlineData("Delete")]
    public async Task LegacyOverloads_UseCancellationTokenNone(string operation)
    {
        var relationalStorage = ExpectSuccess(operation);
        var fixture = new StorageFixture(relationalStorage, operation == "Delete");

        await InvokeLegacyAsync(fixture.Storage, operation, fixture.State);

        var call = Assert.Single(relationalStorage.Calls);
        Assert.Equal(Query(operation), call.Query);
        Assert.Equal(CancellationToken.None, call.CancellationToken);
        AssertSuccessfulState(fixture, operation);
        relationalStorage.VerifyComplete();
    }

    [Theory]
    [InlineData("Read", false)]
    [InlineData("Write", false)]
    [InlineData("Clear", false)]
    [InlineData("Delete", false)]
    [InlineData("Clear", true)]
    [InlineData("Delete", true)]
    public async Task PreCanceledOperations_PreserveStateAndSkipWork(string operation, bool prerequisiteRead)
    {
        var relationalStorage = new ScriptedRelationalStorage();
        var fixture = new StorageFixture(relationalStorage, operation == "Delete", prerequisiteRead);
        var hasher = Substitute.For<IStorageHasherPicker>();
        fixture.Storage.HashPicker = hasher;
        var originalState = fixture.State.State;
        var originalETag = fixture.State.ETag;
        var originalRecordExists = fixture.State.RecordExists;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => InvokeAsync(fixture.Storage, operation, fixture.State, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Same(originalState, fixture.State.State);
        Assert.Equal(originalETag, fixture.State.ETag);
        Assert.Equal(originalRecordExists, fixture.State.RecordExists);
        Assert.Empty(relationalStorage.Calls);
        Assert.Empty(hasher.ReceivedCalls());
        Assert.Empty(fixture.Serializer.ReceivedCalls());
        Assert.Empty(fixture.ActivatorProvider.ReceivedCalls());
        Assert.Empty(fixture.Logger.Entries);
    }

    [Theory]
    [InlineData("Read", false)]
    [InlineData("Write", false)]
    [InlineData("Clear", false)]
    [InlineData("Delete", false)]
    [InlineData("Clear", true)]
    [InlineData("Delete", true)]
    public async Task InFlightCancellation_PreservesExceptionAndStateWithoutErrorLogging(string operation, bool prerequisiteRead)
    {
        var relationalStorage = new BlockingRelationalStorage();
        var fixture = new StorageFixture(relationalStorage, operation == "Delete", prerequisiteRead);
        var originalState = fixture.State.State;
        var originalETag = fixture.State.ETag;
        var originalRecordExists = fixture.State.RecordExists;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var task = InvokeAsync(fixture.Storage, operation, fixture.State, cancellation.Token);
        var observedToken = await relationalStorage.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(cancellation.Token, observedToken);
        Assert.False(task.IsCompleted);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => task.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Same(relationalStorage.CancellationException, exception);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(prerequisiteRead ? Query("Read") : Query(operation), Assert.Single(relationalStorage.Queries));
        Assert.Same(originalState, fixture.State.State);
        Assert.Equal(originalETag, fixture.State.ETag);
        Assert.Equal(originalRecordExists, fixture.State.RecordExists);
        Assert.DoesNotContain(fixture.Logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearStateAsync_ForwardsTokenToPrerequisiteReadAndClear(bool deleteStateOnClear)
    {
        var operation = deleteStateOnClear ? "Delete" : "Clear";
        var relationalStorage = ExpectSuccess("Read")
            .ExpectRead(Query(operation), VersionTable(12));
        var fixture = new StorageFixture(relationalStorage, deleteStateOnClear, prerequisiteRead: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await ((IGrainStorage)fixture.Storage).ClearStateAsync(GrainType, TestGrainId, fixture.State, cancellation.Token);

        Assert.Equal([Query("Read"), Query(operation)], relationalStorage.Calls.Select(call => call.Query));
        Assert.All(relationalStorage.Calls, call => Assert.Equal(cancellation.Token, call.CancellationToken));
        Assert.Equal(
            11,
            ((IDataParameter)relationalStorage.Calls[1].Command.Parameters["GrainStateVersion"]).Value);
        Assert.Equal(deleteStateOnClear ? null : "12", fixture.State.ETag);
        Assert.False(fixture.State.RecordExists);
        Assert.NotSame(fixture.OriginalState, fixture.State.State);
        Assert.NotSame(fixture.StoredState, fixture.State.State);
        Assert.Null(Assert.IsType<TestState>(fixture.State.State).Value);
        relationalStorage.VerifyComplete();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearStateAsync_MissingPrerequisiteRecord_CompletesAfterRead(bool deleteStateOnClear)
    {
        var relationalStorage = new ScriptedRelationalStorage().ExpectRead(Query("Read"));
        var fixture = new StorageFixture(relationalStorage, deleteStateOnClear, prerequisiteRead: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        await ((IGrainStorage)fixture.Storage).ClearStateAsync(GrainType, TestGrainId, fixture.State, cancellation.Token);

        var call = Assert.Single(relationalStorage.Calls);
        Assert.Equal(Query("Read"), call.Query);
        Assert.Equal(cancellation.Token, call.CancellationToken);
        Assert.Null(fixture.State.ETag);
        Assert.False(fixture.State.RecordExists);
        Assert.Null(Assert.IsType<TestState>(fixture.State.State).Value);
        relationalStorage.VerifyComplete();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearStateAsync_CanceledClearPreservesPrerequisiteReadState(bool deleteStateOnClear)
    {
        var operation = deleteStateOnClear ? "Delete" : "Clear";
        var prerequisiteStorage = ExpectSuccess("Read");
        var relationalStorage = new BlockingRelationalStorage(prerequisiteStorage, Query(operation));
        var fixture = new StorageFixture(relationalStorage, deleteStateOnClear, prerequisiteRead: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var task = ((IGrainStorage)fixture.Storage).ClearStateAsync(GrainType, TestGrainId, fixture.State, cancellation.Token);
        var observedToken = await relationalStorage.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(cancellation.Token, observedToken);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => task.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Same(relationalStorage.CancellationException, exception);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal([Query("Read"), Query(operation)], relationalStorage.Queries);
        Assert.Equal(cancellation.Token, Assert.Single(prerequisiteStorage.Calls).CancellationToken);
        Assert.Same(fixture.StoredState, fixture.State.State);
        Assert.Equal("stored", Assert.IsType<TestState>(fixture.State.State).Value);
        Assert.Equal("11", fixture.State.ETag);
        Assert.True(fixture.State.RecordExists);
        Assert.DoesNotContain(fixture.Logger.Entries, entry => entry.Level == LogLevel.Error);
        prerequisiteStorage.VerifyComplete();
    }

    [Theory]
    [InlineData("Read", false)]
    [InlineData("Write", false)]
    [InlineData("Clear", false)]
    [InlineData("Delete", false)]
    [InlineData("Read", true)]
    [InlineData("Write", true)]
    [InlineData("Clear", true)]
    [InlineData("Delete", true)]
    public async Task StorageFailures_AreLoggedAndPreserved(string operation, bool cancellationException)
    {
        Exception expected = cancellationException
            ? new OperationCanceledException("Driver cancellation.")
            : new InvalidOperationException("Storage failure.");
        var relationalStorage = new ScriptedRelationalStorage().ExpectReadException(Query(operation), expected);
        var fixture = new StorageFixture(relationalStorage, operation == "Delete");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var exception = await Record.ExceptionAsync(
            () => InvokeAsync(fixture.Storage, operation, fixture.State, cancellation.Token));

        Assert.Same(expected, exception);
        var error = Assert.Single(fixture.Logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Same(expected, error.Exception);
        Assert.Same(fixture.OriginalState, fixture.State.State);
        Assert.Equal("7", fixture.State.ETag);
        Assert.True(fixture.State.RecordExists);
        Assert.Equal(cancellation.Token, Assert.Single(relationalStorage.Calls).CancellationToken);
        relationalStorage.VerifyComplete();
    }

    [Theory]
    [InlineData("Read")]
    [InlineData("Write")]
    [InlineData("Clear")]
    [InlineData("Delete")]
    public async Task CancellationAfterSuccessfulQuery_UpdatesMetadata(string operation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var relationalStorage = ExpectSuccess(operation);
        relationalStorage.BeforeSelector = cancellation.Cancel;
        var fixture = new StorageFixture(relationalStorage, operation == "Delete");

        await InvokeAsync(fixture.Storage, operation, fixture.State, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(cancellation.Token, Assert.Single(relationalStorage.Calls).CancellationToken);
        AssertSuccessfulState(fixture, operation);
        relationalStorage.VerifyComplete();
    }

    private static Task InvokeAsync(IGrainStorage storage, string operation, GrainState<TestState> state, CancellationToken token)
        => operation switch
        {
            "Read" => storage.ReadStateAsync(GrainType, TestGrainId, state, token),
            "Write" => storage.WriteStateAsync(GrainType, TestGrainId, state, token),
            "Clear" or "Delete" => storage.ClearStateAsync(GrainType, TestGrainId, state, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static Task InvokeLegacyAsync(AdoNetGrainStorage storage, string operation, GrainState<TestState> state)
        => operation switch
        {
            "Read" => storage.ReadStateAsync(GrainType, TestGrainId, state),
            "Write" => storage.WriteStateAsync(GrainType, TestGrainId, state),
            "Clear" or "Delete" => storage.ClearStateAsync(GrainType, TestGrainId, state),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    private static string Query(string operation) => $"{operation} query";

    private static ScriptedRelationalStorage ExpectSuccess(string operation)
    {
        var table = operation == "Read" ? new DataTable() : VersionTable(11);
        if (operation == "Read")
        {
            table.Columns.Add("PayloadBinary", typeof(byte[]));
            table.Columns.Add("Version", typeof(int));
            table.Rows.Add(StorageFixture.Payload, 11);
        }

        return new ScriptedRelationalStorage().ExpectRead(Query(operation), table);
    }

    private static DataTable VersionTable(int version)
    {
        var table = new DataTable();
        table.Columns.Add("NewGrainStateVersion", typeof(int));
        table.Rows.Add(version);
        return table;
    }

    private static void AssertSuccessfulState(StorageFixture fixture, string operation)
    {
        Assert.Equal(operation == "Delete" ? null : "11", fixture.State.ETag);
        Assert.Equal(operation is "Read" or "Write", fixture.State.RecordExists);
        var state = Assert.IsType<TestState>(fixture.State.State);
        if (operation == "Read")
        {
            Assert.Same(fixture.StoredState, state);
            Assert.Equal("stored", state.Value);
        }
        else if (operation == "Write")
        {
            Assert.Same(fixture.OriginalState, state);
            Assert.Equal("original", state.Value);
        }
        else
        {
            Assert.NotSame(fixture.OriginalState, state);
            Assert.Null(state.Value);
        }

        Assert.DoesNotContain(fixture.Logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    private sealed class StorageFixture
    {
        public static readonly byte[] Payload = [1, 2, 3];

        public TestState OriginalState { get; } = new() { Value = "original" };

        public TestState StoredState { get; } = new() { Value = "stored" };

        public GrainState<TestState> State { get; }

        public IGrainStorageSerializer Serializer { get; } = Substitute.For<IGrainStorageSerializer>();

        public IActivatorProvider ActivatorProvider { get; } = Substitute.For<IActivatorProvider>();

        public RecordingLogger Logger { get; } = new();

        public AdoNetGrainStorage Storage { get; }

        public StorageFixture(IRelationalStorage relationalStorage, bool deleteStateOnClear = false, bool prerequisiteRead = false)
        {
            State = new GrainState<TestState>(OriginalState)
            {
                ETag = prerequisiteRead ? null : "7",
                RecordExists = !prerequisiteRead,
            };
            Serializer.Serialize(OriginalState).Returns(new BinaryData(Payload));
            Serializer.Deserialize<TestState>(Arg.Any<BinaryData>()).Returns(StoredState);
            var activator = Substitute.For<IActivator<TestState>>();
            activator.Create().Returns(_ => new TestState());
            ActivatorProvider.GetActivator<TestState>().Returns(activator);
            Serializer.ClearReceivedCalls();
            ActivatorProvider.ClearReceivedCalls();

            Storage = new AdoNetGrainStorage(
                ActivatorProvider,
                Logger,
                Options.Create(new AdoNetGrainStorageOptions
                {
                    GrainStorageSerializer = Serializer,
                    DeleteStateOnClear = deleteStateOnClear,
                }),
                Options.Create(new ClusterOptions { ServiceId = "cancellation-service" }),
                "cancellation-storage")
            {
                CurrentOperationalQueries = new RelationalStorageProviderQueries(
                    Query("Write"), Query("Read"), Query("Clear"), Query("Delete")),
            };
            typeof(AdoNetGrainStorage).GetProperty("Storage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Storage, relationalStorage);
        }
    }

    private sealed class BlockingRelationalStorage(
        IRelationalStorage? precedingStorage = null,
        string? blockedQuery = null) : IRelationalStorage
    {
        public string InvariantName => "Blocking.Provider";

        public string ConnectionString => "blocking";

        public List<string> Queries { get; } = [];

        public TaskCompletionSource<CancellationToken> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OperationCanceledException? CancellationException { get; private set; }

        public async Task<IEnumerable<TResult>> ReadAsync<TResult>(
            string query,
            Action<IDbCommand>? parameterProvider,
            Func<IDataRecord, int, CancellationToken, Task<TResult>> selector,
            CommandBehavior commandBehavior = CommandBehavior.Default,
            CancellationToken cancellationToken = default)
        {
            Queries.Add(query);
            if (precedingStorage is not null && query != blockedQuery)
            {
                return await precedingStorage.ReadAsync(query, parameterProvider, selector, commandBehavior, cancellationToken);
            }

            var completion = new TaskCompletionSource<IEnumerable<TResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() =>
            {
                CancellationException = new OperationCanceledException("Canceled relational operation.", cancellationToken);
                completion.SetException(CancellationException);
            });
            Started.SetResult(cancellationToken);
            return await completion.Task;
        }

        public Task<int> ExecuteAsync(
            string query,
            Action<IDbCommand>? parameterProvider,
            CommandBehavior commandBehavior = CommandBehavior.Default,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : ILogger<AdoNetGrainStorage>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception));
    }

    public sealed class TestState
    {
        public string? Value { get; set; }
    }
}
