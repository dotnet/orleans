using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.AdvancedReminders;
using Orleans.AdvancedReminders.Redis;
using StackExchange.Redis;
using Xunit;
using ClusterOptions = Orleans.Configuration.ClusterOptions;
using ReminderEntry = Orleans.AdvancedReminders.ReminderEntry;

namespace Tester.Redis.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public class RedisAdvancedReminderCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutation_PropagatesCancellationWhileRedisCommandIsStillPending(bool delete)
    {
        var database = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase().Returns(database);
        var upsert = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remove = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>()).Returns(upsert.Task);
        database.SortedSetRemoveRangeByValueAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>()).Returns(remove.Task);
        await using var table = new RedisReminderTable(
            NullLogger<RedisReminderTable>.Instance,
            Options.Create(new ClusterOptions { ServiceId = "cancel-mutation" }),
            Options.Create(new RedisReminderTableOptions { CreateMultiplexer = _ => Task.FromResult((multiplexer, true)) }));
        await table.StartAsync(TestContext.Current.CancellationToken);
        database.ClearReceivedCalls();
        var entry = new ReminderEntry
        {
            GrainId = GrainId.Create("test", "redis-cancellation"),
            ReminderName = "reminder",
            StartAt = DateTime.UtcNow,
        };

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = Mutate(cancellation.Token);
        try
        {
            Assert.Single(database.ReceivedCalls());
            Assert.False(operation.IsCompleted);
            cancellation.Cancel();

            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.False((delete ? (Task)remove.Task : upsert.Task).IsCompleted);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Mutate(cancellation.Token));
            Assert.Single(database.ReceivedCalls());
        }
        finally
        {
            upsert.TrySetResult(RedisResult.Create((RedisValue)1));
            remove.TrySetResult(1);
        }

        Task Mutate(CancellationToken token) => delete
            ? table.RemoveRow(entry.GrainId, entry.ReminderName, "etag", token)
            : table.UpsertRow(entry, token);
    }
}
