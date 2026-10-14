using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.CodeGeneration;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskStorageTests
{
    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public async Task CanceledResponseRecoversFromJournal(string journalFormatKey)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = new DurableTaskJournalHarness(journalFormatKey);
        var taskId = TaskId.Parse("root/canceled");
        var exception = new OperationCanceledException(
            "Durable cancellation requested.",
            new InvalidOperationException("Cancellation source."));
        exception.Data["task"] = taskId.ToString();
        await using (var activation = await harness.ActivateAsync(cancellationToken))
        {
            var storage = activation.Storage;
            var state = storage.GetOrCreateTask(taskId, request: null);
            storage.SetResponse(taskId, state, new CanceledDurableTaskResponse(exception));
            await storage.WriteAsync(cancellationToken);
        }

        await using var recovery = await harness.ActivateAsync(cancellationToken);
        Assert.True(recovery.Storage.TryGetTask(taskId, out var recovered));

        var response = Assert.IsType<CanceledDurableTaskResponse>(recovered.Result);
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Equal("Durable cancellation requested.", response.Exception.Message);
        Assert.Equal(
            "Cancellation source.",
            Assert.IsType<InvalidOperationException>(response.Exception.InnerException).Message);
        Assert.Equal(taskId.ToString(), response.Exception.Data["task"]);
        Assert.True(recovered.CallerId.IsDefault);
        Assert.True(recovered.RemoteTarget.IsDefault);
        Assert.Equal(journalFormatKey, await harness.GetStoredJournalFormatKeyAsync(cancellationToken));
    }

    [Fact]
    public void MutationRejectsStateOwnedByDifferentTaskId()
    {
        var storage = CreateStorage();
        var firstId = TaskId.Parse("root/first");
        var secondId = TaskId.Parse("root/second");
        var firstState = storage.GetOrCreateTask(firstId, request: null);
        var secondState = storage.GetOrCreateTask(secondId, request: null);

        var exception = Assert.Throws<ArgumentException>(
            () => storage.SetResponse(secondId, firstState, DurableTaskResponse.Completed));

        Assert.Equal("state", exception.ParamName);
        Assert.Null(secondState.Result);
        Assert.True(storage.TryGetTask(firstId, out var retainedFirst));
        Assert.Same(firstState, retainedFirst);
        Assert.True(storage.TryGetTask(secondId, out var retainedSecond));
        Assert.Same(secondState, retainedSecond);
    }

    [Fact]
    public void SetRequestRejectsMissingContext()
    {
        var storage = CreateStorage();
        var taskId = TaskId.Parse("root/missing-context");
        var state = storage.GetOrCreateTask(taskId, request: null);
        var request = Substitute.For<IDurableTaskRequest>();

        var exception = Assert.Throws<InvalidOperationException>(
            () => storage.SetRequest(taskId, state, request));

        Assert.Equal("The request context must not be null.", exception.Message);
        Assert.Null(state.Request);
    }

    [Fact]
    public void StoredRequestAndResultAreCachedUntilReplaced()
    {
        var storage = CreateStorage();
        var taskId = TaskId.Parse("root/cached");
        var request = CreateRequest();
        var state = storage.GetOrCreateTask(taskId, request);
        var response = DurableTaskResponse.FromResult(42);

        storage.SetResponse(taskId, state, response);

        var typedState = Assert.IsType<DurableTaskState>(state);
        Assert.Same(request, typedState.Request);
        Assert.Same(response, typedState.Result);
        Assert.NotNull(typedState.RequestPayload);
        Assert.NotNull(typedState.ResultPayload);

        // Replacing a payload discards the cached value, so the next access decodes the stored payload.
        typedState.ResultPayload = typedState.ResultPayload;
        var decoded = Assert.IsType<DurableTaskResponse<int>>(typedState.Result);
        Assert.NotSame(response, decoded);
        Assert.Equal(42, decoded.GetResult<int>());
        Assert.Same(decoded, typedState.Result);
    }

    [Fact]
    public void RetiringACompletionDoesNotDecodeItsResult()
    {
        var storage = CreateStorage();
        var taskId = TaskId.Parse("root/undecodable");
        var destination = GrainId.Create("caller", "undecodable");
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(42));
        storage.AddCompletionDestination(taskId, state, destination);

        // A stored result which no longer decodes, such as one whose type was removed.
        var typedState = Assert.IsType<DurableTaskState>(state);
        typedState.ResultPayload = [0xFF, 0xFF, 0xFF, 0xFF];

        // The outbox dead-letters the completion which it can't materialize, and the runtime retires it.
        storage.RemoveCompletionDestination(taskId, state, destination);
        storage.RetainResult(taskId, state);

        Assert.Empty(state.CompletionDestinations);
        Assert.NotNull(state.RetainedSince);
        Assert.NotNull(state.CompletedAt);
        Assert.ThrowsAny<Exception>(() => state.Result);
    }

    [Fact]
    public void GetChildrenReturnsNearestStoredDescendants()
    {
        var storage = CreateStorage();
        var rootId = TaskId.Parse("root");
        var childId = TaskId.Parse("root/child");
        var grandchildId = TaskId.Parse("root/child/grandchild");
        var operationChildId = TaskId.Parse("root/$when-any-1/0");
        var nestedOperationChildId = TaskId.Parse("root/$when-all-2/0/leaf");
        foreach (var taskId in new[] { grandchildId, rootId, childId, operationChildId, nestedOperationChildId, TaskId.Parse("other") })
        {
            storage.GetOrCreateTask(taskId, request: null);
        }

        // Identifiers between a task and its nearest stored descendants, such as a combinator's operation, aren't stored.
        Assert.Equal(
            new HashSet<TaskId> { childId, operationChildId, nestedOperationChildId },
            storage.GetChildren(rootId).Select(child => child.Id).ToHashSet());
        Assert.Equal([grandchildId], storage.GetChildren(childId).Select(child => child.Id));

        // A removed task's stored descendants become children of its nearest stored ancestor.
        Assert.True(storage.RemoveTask(childId));
        Assert.Equal(
            new HashSet<TaskId> { grandchildId, operationChildId, nestedOperationChildId },
            storage.GetChildren(rootId).Select(child => child.Id).ToHashSet());

        Assert.True(storage.RemoveTask(grandchildId));
        Assert.True(storage.RemoveTask(nestedOperationChildId));
        Assert.Equal([operationChildId], storage.GetChildren(rootId).Select(child => child.Id));
        Assert.Empty(storage.GetChildren(TaskId.Parse("root/$when-all-2")));
    }

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public async Task DeletedTaskAndHierarchyRecoverFromJournal(string journalFormatKey)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = new DurableTaskJournalHarness(journalFormatKey);
        var rootId = TaskId.Parse("root");
        var deletedId = TaskId.Parse("root/remote/deleted");
        var retainedId = TaskId.Parse("root/$when-any-1/0");
        var remoteTarget = GrainId.Create("remote-target", "deleted");
        await using (var activation = await harness.ActivateAsync(cancellationToken))
        {
            var storage = activation.Storage;
            storage.GetOrCreateTask(rootId, request: null);
            storage.GetOrCreateTask(retainedId, request: null);
            var state = storage.GetOrCreateTask(deletedId, CreateRequest());
            storage.SetRemoteRequest(deletedId, state, remoteTarget, "remote-fingerprint");
            storage.SetResponse(deletedId, state, DurableTaskResponse.Completed);
            await storage.WriteAsync(cancellationToken);
        }

        await using (var activation = await harness.ActivateAsync(cancellationToken))
        {
            // The hierarchy index is built from the recovered tasks.
            var storage = activation.Storage;
            Assert.Equal(
                new HashSet<TaskId> { deletedId, retainedId },
                storage.GetChildren(rootId).Select(child => child.Id).ToHashSet());
            Assert.True(storage.RemoveTask(deletedId));
            Assert.Equal([retainedId], storage.GetChildren(rootId).Select(child => child.Id));
            await storage.WriteAsync(cancellationToken);
        }

        await using var recovery = await harness.ActivateAsync(cancellationToken);
        Assert.False(recovery.Storage.TryGetTask(deletedId, out _));
        Assert.Equal([retainedId], recovery.Storage.GetChildren(rootId).Select(child => child.Id));
    }
    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public async Task FullTerminalStateRecoversFromJournal(string journalFormatKey)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var createdAt = new DateTimeOffset(2041, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var clock = new FakeTimeProvider(createdAt);
        await using var harness = new DurableTaskJournalHarness(journalFormatKey, clock);
        var taskId = TaskId.Parse("root/remote/terminal");
        var terminalResult = TaskId.Parse("root/result/42");
        var remoteTarget = GrainId.Create("remote-target", "alpha");
        var caller = GrainId.Create("caller", "beta");
        var completionDestination = GrainId.Create("completion-destination", "gamma");
        await using (var activation = await harness.ActivateAsync(cancellationToken))
        {
            var storage = activation.Storage;
            var state = storage.GetOrCreateTask(taskId, request: null);
            storage.SetRequestFingerprint(taskId, state, "request-fingerprint-v1");
            storage.SetRemoteRequest(taskId, state, remoteTarget, "remote-fingerprint-v2");
            storage.SetCallerId(taskId, state, caller);
            storage.AddCompletionDestination(taskId, state, completionDestination);
            storage.SetDelay(taskId, state, createdAt.AddMinutes(10), generation: 3);
            await storage.WriteAsync(cancellationToken);
            clock.Advance(TimeSpan.FromMinutes(3));
            storage.SetResponse(taskId, state, DurableTaskResponse.FromResult(terminalResult));
            await storage.WriteAsync(cancellationToken);
            clock.Advance(TimeSpan.FromMinutes(5));
            storage.RetainResult(taskId, state);
            await storage.WriteAsync(cancellationToken);
        }

        await using var recovery = await harness.ActivateAsync(cancellationToken);
        Assert.True(recovery.Storage.TryGetTask(taskId, out var recovered));

        Assert.Null(recovered.Request);
        Assert.Equal(createdAt, recovered.CreatedAt);
        Assert.Equal(createdAt.AddMinutes(3), recovered.CompletedAt);
        Assert.Equal(createdAt.AddMinutes(8), recovered.RetainedSince);
        Assert.Null(recovered.CancellationRequestedAt);
        Assert.Null(recovered.DueTime);
        Assert.Equal(4, recovered.ResumeGeneration);
        Assert.Equal("request-fingerprint-v1", recovered.RequestFingerprint);
        Assert.Equal(remoteTarget, recovered.RemoteTarget);
        Assert.Equal("remote-fingerprint-v2", recovered.RemoteRequestFingerprint);
        Assert.Equal(caller, recovered.CallerId);
        Assert.Equal(completionDestination, Assert.Single(recovered.CompletionDestinations));
        var response = Assert.IsType<DurableTaskResponse<TaskId>>(recovered.Result);
        Assert.Equal(terminalResult, response.GetResult<TaskId>());
    }

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public async Task RequestsAndTerminalResponsesRecoverFromJournal(string journalFormatKey)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = new DurableTaskJournalHarness(journalFormatKey);
        var pendingId = TaskId.CreateRoot("pending").Child("child/with\\escapes");
        var succeededId = TaskId.Parse("root/succeeded");
        var failedId = TaskId.Parse("root/failed");
        var canceledId = TaskId.Parse("root/canceled");
        var caller = GrainId.Create("caller", "request");
        var target = GrainId.Create("target", "request");
        var request = CreateRequest(caller, target);
        var failure = new InvalidOperationException("The durable operation failed.");
        failure.Data["attempt"] = 3;
        var cancellation = new OperationCanceledException("The durable operation was canceled.");
        await using (var activation = await harness.ActivateAsync(cancellationToken))
        {
            var storage = activation.Storage;
            storage.GetOrCreateTask(pendingId, request);
            var succeeded = storage.GetOrCreateTask(succeededId, request: null);
            storage.SetRequest(succeededId, succeeded, CreateRequest(caller, target));
            storage.SetResponse(succeededId, succeeded, DurableTaskResponse.FromResult("typed result"));
            await storage.WriteAsync(cancellationToken);

            var failed = storage.GetOrCreateTask(failedId, request: null);
            storage.SetResponse(failedId, failed, DurableTaskResponse.FromException(failure));
            var canceled = storage.GetOrCreateTask(canceledId, request: null);
            storage.SetResponse(canceledId, canceled, new CanceledDurableTaskResponse(cancellation));
            await storage.WriteAsync(cancellationToken);
        }

        await using var recovery = await harness.ActivateAsync(cancellationToken);
        var recoveredStorage = recovery.Storage;
        Assert.Equal(
            new[] { pendingId, canceledId, failedId, succeededId },
            recoveredStorage.Tasks.Select(static task => task.Id).OrderBy(static id => id.ToString(), StringComparer.Ordinal));

        Assert.True(recoveredStorage.TryGetTask(pendingId, out var pending));
        var pendingRequest = Assert.IsType<TestDurableTaskRequest>(pending.Request);
        Assert.Equal(caller, pendingRequest.Context!.CallerId);
        Assert.Equal(target, pendingRequest.Context.TargetId);
        Assert.True(pendingRequest.Context.SupportsDurableCompletion);
        Assert.Equal(new byte[] { 1, 2, 3 }, pendingRequest.Context.Values!["header"]);
        Assert.Equal(InvokeMethodOptionsForRequest, pendingRequest.Options);
        Assert.Null(pending.Result);
        Assert.Null(pending.CompletedAt);

        Assert.True(recoveredStorage.TryGetTask(succeededId, out var succeededState));
        Assert.Equal(caller, Assert.IsType<TestDurableTaskRequest>(succeededState.Request).Context!.CallerId);
        var success = Assert.IsType<DurableTaskResponse<string>>(succeededState.Result);
        Assert.Equal(DurableTaskStatus.CompletedSuccessfully, success.Status);
        Assert.Equal("typed result", success.GetResult<string>());
        Assert.NotNull(succeededState.CompletedAt);

        Assert.True(recoveredStorage.TryGetTask(failedId, out var failedState));
        Assert.Null(failedState.Request);
        var failedResponse = Assert.IsType<ExceptionDurableTaskResponse>(failedState.Result);
        Assert.Equal(DurableTaskStatus.Failed, failedResponse.Status);
        var recoveredFailure = Assert.IsType<InvalidOperationException>(failedResponse.Exception);
        Assert.Equal("The durable operation failed.", recoveredFailure.Message);
        Assert.Equal(3, recoveredFailure.Data["attempt"]);

        Assert.True(recoveredStorage.TryGetTask(canceledId, out var canceledState));
        var canceledResponse = Assert.IsType<CanceledDurableTaskResponse>(canceledState.Result);
        Assert.Equal(DurableTaskStatus.Canceled, canceledResponse.Status);
        Assert.Equal("The durable operation was canceled.", canceledResponse.Exception.Message);
        Assert.Equal(journalFormatKey, await harness.GetStoredJournalFormatKeyAsync(cancellationToken));
    }

    [Theory]
    [MemberData(nameof(DurableTaskJournalHarness.Formats), MemberType = typeof(DurableTaskJournalHarness))]
    public async Task MetadataUpdatesJournalEachPayloadOnce(string journalFormatKey)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = new DurableTaskJournalHarness(journalFormatKey);
        var taskId = TaskId.Parse("root/payloads");
        var caller = GrainId.Create("caller", "payloads");
        byte[] requestPayload;
        byte[] resultPayload;
        await using (var activation = await harness.ActivateAsync(cancellationToken))
        {
            var storage = activation.Storage;
            var state = storage.GetOrCreateTask(taskId, CreateRequest(caller, GrainId.Create("target", "payloads")));
            storage.SetCallerId(taskId, state, caller);
            storage.SetRequestFingerprint(taskId, state, "fingerprint");
            storage.AddCompletionDestination(taskId, state, caller);
            await storage.WriteAsync(cancellationToken);
            storage.SetResponse(taskId, state, DurableTaskResponse.FromResult("payload result"));
            storage.RemoveCompletionDestination(taskId, state, caller);
            await storage.WriteAsync(cancellationToken);
            var typedState = Assert.IsType<DurableTaskState>(state);
            requestPayload = typedState.RequestPayload!;
            resultPayload = typedState.ResultPayload!;
        }

        var journal = await ReadJournalAsync(harness, cancellationToken);
        Assert.Equal(1, CountPayload(journal, requestPayload));
        Assert.Equal(1, CountPayload(journal, resultPayload));

        await using (var recovery = await harness.ActivateAsync(cancellationToken))
        {
            Assert.True(recovery.Storage.TryGetTask(taskId, out var recovered));
            Assert.Equal(caller, Assert.IsType<TestDurableTaskRequest>(recovered.Request).Context!.CallerId);
            Assert.Equal("payload result", recovered.Result!.GetResult<string>());
            Assert.Empty(recovered.CompletionDestinations);
            Assert.True(recovery.Storage.RemoveTask(taskId));
            await recovery.Storage.WriteAsync(cancellationToken);
        }

        // The deletion is journaled.
        await using var deleted = await harness.ActivateAsync(cancellationToken);
        Assert.False(deleted.Storage.TryGetTask(taskId, out _));
        Assert.Empty(deleted.Storage.Tasks);
    }

    private static async Task<byte[]> ReadJournalAsync(DurableTaskJournalHarness harness, CancellationToken cancellationToken)
    {
        var consumer = new JournalBytes();
        await harness.StorageProvider.CreateStorage(harness.JournalId).ReadAsync(consumer, cancellationToken);
        return consumer.Bytes.ToArray();
    }

    // The JSON journal format writes a byte array as a base64 string.
    private static int CountPayload(byte[] journal, byte[] payload) =>
        CountOccurrences(journal, payload) + CountOccurrences(journal, System.Text.Encoding.UTF8.GetBytes(Convert.ToBase64String(payload)));

    private static int CountOccurrences(ReadOnlySpan<byte> data, ReadOnlySpan<byte> value)
    {
        var count = 0;
        for (var index = data.IndexOf(value); index >= 0; index = data.IndexOf(value))
        {
            count++;
            data = data[(index + value.Length)..];
        }

        return count;
    }

    private sealed class JournalBytes : IJournalStorageConsumer
    {
        public MemoryStream Bytes { get; } = new();

        public void Read(JournalBufferReader buffer, IJournalMetadata? metadata)
        {
            while (buffer.Length > 0)
            {
                var chunk = new byte[buffer.Length];
                buffer.Read(chunk);
                Bytes.Write(chunk);
            }
        }
    }

    private const InvokeMethodOptions InvokeMethodOptionsForRequest = InvokeMethodOptions.ReadOnly;

    private static TestDurableTaskRequest CreateRequest(GrainId caller = default, GrainId target = default)
    {
        var request = new TestDurableTaskRequest
        {
            Context = new DurableTaskRequestContext
            {
                CallerId = caller,
                TargetId = target,
                SupportsDurableCompletion = true,
                Values = new() { ["header"] = [1, 2, 3] },
            },
        };
        request.AddInvokeMethodOptions(InvokeMethodOptionsForRequest);
        return request;
    }

    private static DurableTaskGrainStorage CreateStorage() =>
        new(
            new TestDurableDictionary<TaskId, DurableTaskState>(),
            new TestDurableDictionary<TaskId, byte[]>(),
            new TestDurableDictionary<TaskId, byte[]>(),
            new TestStateManager(),
            TimeProvider.System,
            CreateSerializer());

    private static Serializer CreateSerializer()
    {
        var services = new ServiceCollection();
        services.AddSerializer(builder =>
        {
            builder.AddAssembly(typeof(DurableTaskState).Assembly);
            builder.AddAssembly(typeof(DurableTaskStorageTests).Assembly);
        });
        return services.BuildServiceProvider().GetRequiredService<Serializer>();
    }

    private sealed class TestDurableDictionary<TKey, TValue> : Dictionary<TKey, TValue>, IDurableDictionary<TKey, TValue>
        where TKey : notnull;

    private sealed class TestStateManager : IJournaledStateManager
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => default;
        public void RegisterStateMachine(string name, IStateMachine stateMachine) { }
        public bool TryGetStateMachine(string name, [NotNullWhen(true)] out IStateMachine? stateMachine)
        {
            stateMachine = null;
            return false;
        }

        public ValueTask WriteStateAsync(CancellationToken cancellationToken) => default;
        public ValueTask DeleteStateAsync(CancellationToken cancellationToken) => default;
    }
}
