#nullable enable
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.AdvancedReminders.Redis;
using StackExchange.Redis;
using Xunit;
using ClusterOptions = Orleans.Configuration.ClusterOptions;

namespace Tester.Redis.AdvancedReminders;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Reminders")]
public class RedisAdvancedReminderCountTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(34_900L)]
    [InlineData(3_000_000_000L)]
    public async Task Count_ReadsOnlyTheConfiguredServiceSortedSet(long count)
    {
        var database = Substitute.For<IDatabase>();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase().Returns(database);
        database.SortedSetLengthAsync((RedisKey)"service/advanced-reminders").Returns(Task.FromResult(count));
        await using var table = new RedisReminderTable(
            NullLogger<RedisReminderTable>.Instance,
            Options.Create(new ClusterOptions { ServiceId = "service" }),
            Options.Create(new RedisReminderTableOptions
            {
                CreateMultiplexer = _ => Task.FromResult((multiplexer, true)),
                TableExpiry = ReminderTableExpiry.Never,
            }));
        await table.StartAsync(TestContext.Current.CancellationToken);
        database.ClearReceivedCalls();

        Assert.Equal(count, await table.GetCountAsync(TestContext.Current.CancellationToken));
        var call = Assert.Single(database.ReceivedCalls());
        Assert.Equal(nameof(IDatabase.SortedSetLengthAsync), call.GetMethodInfo().Name);
        Assert.Equal((RedisKey)"service/advanced-reminders", call.GetArguments()[0]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            table.GetCountAsync(new CancellationToken(canceled: true)));
        Assert.Single(database.ReceivedCalls());
    }
}
