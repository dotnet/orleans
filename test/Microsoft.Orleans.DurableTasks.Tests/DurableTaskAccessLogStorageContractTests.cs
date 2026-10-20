using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Configuration;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Journaling;
using Orleans.Messaging;
using Orleans.Messaging.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>
/// Verifies the contract of every durable task storage which records the accesses of durable task bodies, and that the
/// runtime binds to an activation only when its accesses can be recorded.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskAccessLogStorageContractTests
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    public static TheoryData<string> Storages => new() { DurableTaskJournalHarness.Json, DurableTaskJournalHarness.OrleansBinary };

    [Theory]
    [MemberData(nameof(Storages))]
    public async Task RecordsCommitWithTheNextWriteAndPersistUntilTheResultIsStored(string kind)
    {
        var taskId = TaskId.Parse("root/access-log");
        await using var fixture = await StorageFixture.CreateAsync(kind);
        var storage = fixture.Storage;
        var state = storage.GetOrCreateTask(taskId, DurableTaskRecordKind.LocalStep, request: null);
        Logs(storage).AppendAccessLog(taskId, [1, 2]);

        // A request which a later run stores again keeps the execution's records.
        storage.SetRequest(taskId, state, new TestDurableTaskRequest { Context = new() });
        await storage.WriteAsync(TestCancellation);
        Logs(storage).AppendAccessLog(taskId, [3]);

        // The append which no write committed is lost with the activation, and the committed records recover.
        storage = await fixture.RestartAsync();
        Assert.Equal([1, 2], Logs(storage).GetAccessLog(taskId));

        Assert.True(storage.TryGetTask(taskId, out state));
        storage.SetResponse(taskId, state, DurableTaskResponse.Completed);
        Assert.Empty(Logs(storage).GetAccessLog(taskId));
        await storage.WriteAsync(TestCancellation);

        storage = await fixture.RestartAsync();
        Assert.Empty(Logs(storage).GetAccessLog(taskId));
    }

    [Theory]
    [MemberData(nameof(Storages))]
    public async Task ReturnedRecordsDontChangeWithLaterAppendsAndRemovalDiscardsThem(string kind)
    {
        var taskId = TaskId.Parse("root/access-log");
        await using var fixture = await StorageFixture.CreateAsync(kind);
        var storage = fixture.Storage;
        _ = storage.GetOrCreateTask(taskId, DurableTaskRecordKind.LocalStep, request: null);
        Logs(storage).AppendAccessLog(taskId, [1]);
        var returned = Logs(storage).GetAccessLog(taskId);
        for (var i = 2; i <= 20; i++)
        {
            Logs(storage).AppendAccessLog(taskId, [(byte)i]);
        }

        Assert.Equal([1], returned);
        Assert.Equal(Enumerable.Range(1, 20).Select(static i => (byte)i), Logs(storage).GetAccessLog(taskId));

        Assert.True(storage.RemoveTask(taskId));
        Assert.Empty(Logs(storage).GetAccessLog(taskId));
    }

    [Fact]
    public void RuntimeFailsToBindToStorageWhichDoesntStoreAccessLogs()
    {
        var runtime = CreateRuntime(Substitute.For<IDurableTaskGrainStorage>(), new ContextHostStateManager());

        var exception = Assert.Throws<InvalidOperationException>(() => runtime.InitializeForActivation(Substitute.For<IGrainContext>()));
        Assert.Contains("access logs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeFailsToBindToAManagerWhichDoesntRouteAccesses()
    {
        var runtime = CreateRuntime(new AccessLogStorage(), Substitute.For<IJournaledStateManager>());

        var exception = Assert.Throws<InvalidOperationException>(() => runtime.InitializeForActivation(Substitute.For<IGrainContext>()));
        Assert.Contains("route", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeFailsToBindWhenAnotherHostsContextRoutesAccesses()
    {
        var manager = new ContextHostStateManager { ContextProvider = new OtherHostsProvider() };
        var runtime = CreateRuntime(new AccessLogStorage(), manager);

        var exception = Assert.Throws<InvalidOperationException>(() => runtime.InitializeForActivation(Substitute.For<IGrainContext>()));
        Assert.Contains("already run through", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundRuntimeInstallsItsContextProvider()
    {
        var manager = new ContextHostStateManager();
        var runtime = CreateRuntime(new AccessLogStorage(), manager);

        runtime.InitializeForActivation(Substitute.For<IGrainContext>());

        Assert.IsType<DurableTaskAccessContextProvider>(manager.ContextProvider);
    }

    private static IDurableTaskAccessLogStorage Logs(IDurableTaskGrainStorage storage) => Assert.IsAssignableFrom<IDurableTaskAccessLogStorage>(storage);

    private static DurableTaskGrainRuntime CreateRuntime(IDurableTaskGrainStorage storage, IJournaledStateManager manager)
    {
        var context = Substitute.For<IGrainContext>();
        context.GrainId.Returns(GrainId.Create("test", "one"));
        var accessor = Substitute.For<IGrainContextAccessor>();
        accessor.GrainContext.Returns(context);
        var shared = new DurableTaskGrainRuntimeShared(
            accessor,
            TimeProvider.System,
            NullLogger<DurableTaskGrainRuntime>.Instance,
            Options.Create(new DurableTaskOptions()),
            Options.Create(new DurableMessagingOptions()),
            MessagingTestHelpers.Serializer);
        return new DurableTaskGrainRuntime(
            storage,
            new TestDurableDictionary<TaskId, DateTimeOffset>(),
            new TestDurableDictionary<MessageId, PendingInvocation>(),
            shared,
            [],
            manager);
    }

    /// <summary>The journaled storage in one journal format, which restarts as a new activation does.</summary>
    private sealed class StorageFixture(DurableTaskJournalHarness harness, DurableTaskJournalHarness.Activation activation) : IAsyncDisposable
    {
        private DurableTaskJournalHarness.Activation _activation = activation;

        public IDurableTaskGrainStorage Storage => _activation.Storage;

        public static async Task<StorageFixture> CreateAsync(string format)
        {
            var harness = new DurableTaskJournalHarness(format);
            return new StorageFixture(harness, await harness.ActivateAsync(TestCancellation));
        }

        /// <summary>Recovers the storage from what its writes persisted.</summary>
        public async Task<IDurableTaskGrainStorage> RestartAsync()
        {
            await _activation.DisposeAsync();
            _activation = await harness.ActivateAsync(TestCancellation);
            return _activation.Storage;
        }

        public async ValueTask DisposeAsync()
        {
            await _activation.DisposeAsync();
            await harness.DisposeAsync();
        }
    }

    private sealed class AccessLogStorage : IDurableTaskGrainStorage, IDurableTaskAccessLogStorage
    {
        private readonly IDurableTaskGrainStorage _tasks = Substitute.For<IDurableTaskGrainStorage>();

        public IEnumerable<(TaskId Id, IDurableTaskState State)> Tasks => _tasks.Tasks;
        public byte[] GetAccessLog(TaskId taskId) => [];
        public void AppendAccessLog(TaskId taskId, ReadOnlySpan<byte> records) { }
        public IEnumerable<(TaskId Id, IDurableTaskState State)> GetChildren(TaskId task) => _tasks.GetChildren(task);
        public IDurableTaskState GetOrCreateTask(TaskId taskId, DurableTaskRecordKind kind, IDurableTaskRequest? request) => _tasks.GetOrCreateTask(taskId, kind, request);
        public void SetRequest(TaskId taskId, IDurableTaskState state, IDurableTaskRequest request) { }
        public void SetRequestFingerprint(TaskId taskId, IDurableTaskState state, string fingerprint, string? method = null) { }
        public void SetRemoteRequest(TaskId taskId, IDurableTaskState state, GrainId target, string method, string fingerprint) { }
        public void SetCallerId(TaskId taskId, IDurableTaskState state, GrainId callerId) { }
        public void SetIncarnation(TaskId taskId, IDurableTaskState state, Guid incarnation) { }
        public void SetInvocation(TaskId taskId, IDurableTaskState state) { }
        public void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response) { }
        public void RequestCancellation(TaskId taskId, IDurableTaskState state, string? reason = null) { }
        public void Shield(TaskId taskId, IDurableTaskState state) { }
        public void SetAccepted(TaskId taskId, IDurableTaskState state, bool accepted) { }
        public void SetDelay(TaskId taskId, IDurableTaskState state, DateTimeOffset dueTime, long generation) { }
        public void AddCompletionDestination(TaskId taskId, IDurableTaskState state, DurableTaskSubscriber destination) { }
        public void RemoveCompletionDestination(TaskId taskId, IDurableTaskState state, DurableTaskSubscriber destination) { }
        public void RetainResult(TaskId taskId, IDurableTaskState state) { }
        public bool TryGetTask(TaskId taskId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IDurableTaskState? state) => _tasks.TryGetTask(taskId, out state);
        public bool RemoveTask(TaskId taskId) => false;
        public void Clear() { }
        public ValueTask WriteAsync(CancellationToken cancellationToken) => default;
        public ValueTask ReadAsync(CancellationToken cancellationToken) => default;
    }

    private sealed class TestDurableDictionary<TKey, TValue> : Dictionary<TKey, TValue>, IDurableDictionary<TKey, TValue>
        where TKey : notnull;

    private sealed class OtherHostsProvider : JournaledStateContextProvider
    {
        public override JournaledStateContext? Current => null;
    }

    private sealed class ContextHostStateManager : IJournaledStateManager, IJournaledStateContextHost
    {
        public JournaledStateContextProvider? ContextProvider { get; set; }

        public void EnterHostAccess()
        {
        }

        public void ExitHostAccess()
        {
        }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;

        public void RegisterStateMachine(string name, IStateMachine stateMachine) { }

        public bool TryGetStateMachine(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IStateMachine? stateMachine)
        {
            stateMachine = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => default;

        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }
}
