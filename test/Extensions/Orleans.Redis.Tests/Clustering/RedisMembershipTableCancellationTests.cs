using System.Net;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.Clustering.Redis;
using Orleans.Configuration;
using Orleans.Runtime;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Clustering;

[TestSuite("BVT")]
[TestProvider("Redis")]
[TestArea("Membership")]
[TestCategory("BVT")]
public sealed class RedisMembershipTableCancellationTests
{
    [Theory]
    [InlineData("Initialize")]
    [InlineData("Delete")]
    [InlineData("ReadAll")]
    [InlineData("ReadRow")]
    [InlineData("Insert")]
    [InlineData("Update")]
    [InlineData("Heartbeat")]
    [InlineData("Cleanup")]
    public async Task Operations_PreCanceled_DoNotAccessBackend(string operation)
    {
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            throw new InvalidOperationException("The connection factory must not be called.");
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var token = cancellation.Token;
        var entry = new MembershipEntry { SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1) };
        var version = new TableVersion(1, "1");

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "Initialize" => table.InitializeMembershipTableAsync(true, token),
            "Delete" => table.DeleteMembershipTableEntriesAsync("cluster", token),
            "ReadAll" => table.ReadAllAsync(token),
            "ReadRow" => table.ReadRowAsync(entry.SiloAddress, token),
            "Insert" => table.InsertRowAsync(entry, version, token),
            "Update" => table.UpdateRowAsync(entry, "0", version, token),
            "Heartbeat" => table.UpdateIAmAliveAsync(entry, token),
            "Cleanup" => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        Assert.Equal(token, exception.CancellationToken);
        Assert.Equal(0, factoryCalls);
        Assert.False(table.IsInitialized);
    }

    [Fact]
    public async Task Initialize_CanceledDuringFactory_DisposesLateOwnedConnection()
    {
        var creation = new TaskCompletionSource<(IConnectionMultiplexer, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var muxer = Substitute.For<IConnectionMultiplexer>();
        muxer.DisposeAsync().Returns(_ =>
        {
            disposed.SetResult();
            return ValueTask.CompletedTask;
        });
        using var table = CreateTable(_ => creation.Task);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var initialization = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.False(initialization.IsCompleted);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initialization.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(creation.Task.IsCompleted);
        Assert.False(table.IsInitialized);
        table.Dispose();

        creation.SetResult((muxer, false));
        await disposed.Task.WaitAsync(TestContext.Current.CancellationToken);

        await muxer.Received(1).DisposeAsync();
        muxer.DidNotReceive().GetDatabase(Arg.Any<int>(), Arg.Any<object>());
        Assert.False(table.IsInitialized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_CanceledDuringTableVersion_DoesNotExpireOrPublishConnection(bool isShared)
    {
        var versionWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Substitute.For<IDatabase>();
        database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), When.NotExists)
            .Returns(versionWrite.Task);
        var muxer = Substitute.For<IConnectionMultiplexer>();
        muxer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        muxer.DisposeAsync().Returns(ValueTask.CompletedTask);
        using var table = CreateTable(_ => Task.FromResult((muxer, isShared)));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var initialization = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.False(initialization.IsCompleted);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initialization.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(table.IsInitialized);
        Assert.False(versionWrite.Task.IsCompleted);
        await muxer.Received(isShared ? 0 : 1).DisposeAsync();
        await database.DidNotReceive().KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>());
        versionWrite.SetResult(true);
    }

    [Fact]
    public async Task Initialize_CompletedStorageWork_PublishesConnectionAfterCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var database = Substitute.For<IDatabase>();
        database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), When.NotExists)
            .Returns(Task.FromResult(true));
        database.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>()).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult(true);
        });
        var muxer = Substitute.For<IConnectionMultiplexer>();
        muxer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        using var table = CreateTable(_ => Task.FromResult((muxer, true)));

        await table.InitializeMembershipTableAsync(true, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(table.IsInitialized);
        await muxer.DidNotReceive().DisposeAsync();
    }

    [Fact]
    public async Task ReadRow_CanceledDuringTransaction_DoesNotReturnSuccess()
    {
        var execution = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transaction = Substitute.For<ITransaction>();
        transaction.ExecuteAsync().Returns(execution.Task);
        var database = Substitute.For<IDatabase>();
        database.CreateTransaction().Returns(transaction);
        var muxer = Substitute.For<IConnectionMultiplexer>();
        muxer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        using var table = CreateTable(_ => Task.FromResult((muxer, true)));
        await table.InitializeMembershipTableAsync(false, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var read = table.ReadRowAsync(SiloAddress.New(IPAddress.Loopback, 11111, 1), cancellation.Token);
        Assert.False(read.IsCompleted);

        cancellation.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => read.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(execution.Task.IsCompleted);
        _ = transaction.Received(1).ExecuteAsync();
        execution.SetResult(true);
    }

    private static RedisMembershipTable CreateTable(Func<RedisClusteringOptions, Task<(IConnectionMultiplexer, bool)>> factory) =>
        new(
            Options.Create(new RedisClusteringOptions
            {
                CreateMultiplexer = factory,
                EntryExpiry = TimeSpan.FromHours(1)
            }),
            Options.Create(new ClusterOptions { ServiceId = "service", ClusterId = "cluster" }));
}
