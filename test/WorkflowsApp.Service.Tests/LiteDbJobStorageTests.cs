using System.Distributed.DurableTasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using PaymentWorkflowApp.Runtime;
using Xunit;

namespace WorkflowsApp.Service.Tests;

[TestCategory("BVT")]
public sealed class LiteDbJobStorageTests
{
    [Fact]
    public async Task FailedDeleteThenReAddPersistsReAddedTask()
    {
        var databasePath = Path.Combine(AppContext.BaseDirectory, $"jobs-{Guid.NewGuid():N}.db");
        try
        {
            using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
            var serializer = services.GetRequiredService<Serializer<JobTaskState>>();
            var copier = services.GetRequiredService<DeepCopier<JobTaskState>>();
            var taskId = TaskId.Create("re-added");

            using (var storage = new LiteDbJobStorage(serializer, copier, databasePath))
            {
                storage.AddOrUpdateTask(taskId, new JobTaskState { Type = "initial" });
                await storage.WriteAsync(CancellationToken.None);
                Assert.True(storage.RemoveTask(taskId));

                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(
                    () => storage.WriteAsync(cancellation.Token).AsTask());

                storage.AddOrUpdateTask(taskId, new JobTaskState { Type = "replacement" });
                await storage.WriteAsync(CancellationToken.None);
            }

            using var reloaded = new LiteDbJobStorage(serializer, copier, databasePath);
            await reloaded.ReadAsync(CancellationToken.None);
            Assert.True(reloaded.TryGetTask(taskId, out var state));
            Assert.Equal("replacement", state.Type);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }
}
