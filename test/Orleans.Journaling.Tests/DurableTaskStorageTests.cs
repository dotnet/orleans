using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.DurableTasks.Storage;
using Orleans.DurableTasks;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Journaling")]
public sealed class DurableTaskStorageTests : JournalingTestBase
{
    [Fact]
    public async Task CanceledResponseRoundTripsThroughJournaledStorage()
    {
        var initial = CreateTestSystem();
        var storage = CreateTaskStorage(initial.Manager);
        await initial.Lifecycle.OnStart();
        var taskId = TaskId.Parse("root/canceled");
        var exception = new OperationCanceledException(
            "Durable cancellation requested.",
            new InvalidOperationException("Cancellation source."));
        exception.Data["task"] = "root/canceled";
        var state = storage.GetOrCreateTask(taskId, request: null);
        storage.SetResponse(taskId, state, new CanceledDurableTaskResponse(exception));
        await storage.WriteAsync(default);

        var recovered = CreateTestSystem(storage: initial.Storage);
        var recoveredStorage = CreateTaskStorage(recovered.Manager);
        await recovered.Lifecycle.OnStart();

        Assert.True(recoveredStorage.TryGetTask(taskId, out var recoveredState));
        var response = Assert.IsType<CanceledDurableTaskResponse>(recoveredState.Result);
        Assert.Equal(DurableTaskStatus.Canceled, response.Status);
        Assert.Equal("Durable cancellation requested.", response.Exception.Message);
        Assert.Equal(
            "Cancellation source.",
            Assert.IsType<InvalidOperationException>(response.Exception.InnerException).Message);
        Assert.Equal("root/canceled", response.Exception.Data["task"]);
    }

    private DurableTaskGrainStorage CreateTaskStorage(IJournaledStateManager manager)
    {
        var tasks = new DurableDictionary<TaskId, DurableTaskState>(
            "$tasks",
            manager,
            new OrleansBinaryDurableDictionaryCommandCodec<TaskId, DurableTaskState>(
                CodecProvider.GetCodec<TaskId>(),
                CodecProvider.GetCodec<DurableTaskState>(),
                SessionPool));
        return new DurableTaskGrainStorage(tasks, manager, TimeProvider.System);
    }
}
