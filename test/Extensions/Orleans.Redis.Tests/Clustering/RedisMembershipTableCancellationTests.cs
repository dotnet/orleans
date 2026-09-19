using System.Net;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
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
    [InlineData("InsertWithResult")]
    [InlineData("UpdateWithResult")]
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
            "InsertWithResult" => table.InsertRowWithResultAsync(entry, version, token),
            "UpdateWithResult" => table.UpdateRowWithResultAsync(entry, "0", version, token),
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
    public async Task Initialize_FactoryFailure_PropagatesOriginalExceptionAndAllowsRetry(bool synchronous)
    {
        var backend = new MembershipBackend();
        var failure = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Factory failed.");
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            if (++factoryCalls == 1)
            {
                if (synchronous)
                {
                    throw failure;
                }

                return Task.FromException<(IConnectionMultiplexer, bool)>(failure);
            }

            return Task.FromResult((backend.Multiplexer, false));
        });
        var token = TestContext.Current.CancellationToken;

        Assert.Same(failure, await Assert.ThrowsAsync<RedisConnectionException>(
            () => table.InitializeMembershipTableAsync(true, token)));
        Assert.False(table.IsInitialized);
        await backend.Multiplexer.DidNotReceive().DisposeAsync();

        await table.InitializeMembershipTableAsync(true, token);

        Assert.True(table.IsInitialized);
        Assert.Equal(2, factoryCalls);
        Assert.Equal((RedisValue)"0", Assert.Single(backend.Rows).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Initialize_DisposedDuringFactory_RejectsLatePublicationAndWaitingCalls(bool isShared, bool disposeAsync)
    {
        var backend = new MembershipBackend();
        var creation = new TaskCompletionSource<(IConnectionMultiplexer, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            return creation.Task;
        });
        var token = TestContext.Current.CancellationToken;
        var first = table.InitializeMembershipTableAsync(true, token);
        var waiting = table.InitializeMembershipTableAsync(true, token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var canceledWaiter = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.False(first.IsCompleted);
        Assert.False(waiting.IsCompleted);
        Assert.False(canceledWaiter.IsCompleted);

        if (disposeAsync)
        {
            await table.DisposeAsync();
        }
        else
        {
            table.Dispose();
        }

        cancellation.Cancel();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter.WaitAsync(token));
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        var subsequent = await Assert.ThrowsAsync<ObjectDisposedException>(() => table.InitializeMembershipTableAsync(true, token));
        Assert.Equal(typeof(RedisMembershipTable).FullName, subsequent.ObjectName);
        Assert.False(table.IsInitialized);
        Assert.False(creation.Task.IsCompleted);
        Assert.Equal(1, factoryCalls);

        creation.SetResult((backend.Multiplexer, isShared));

        var firstError = await Assert.ThrowsAsync<ObjectDisposedException>(() => first.WaitAsync(token));
        var waitingError = await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(token));
        Assert.Equal(typeof(RedisMembershipTable).FullName, firstError.ObjectName);
        Assert.Equal(typeof(RedisMembershipTable).FullName, waitingError.ObjectName);
        Assert.False(table.IsInitialized);
        Assert.Equal(1, factoryCalls);
        Assert.Empty(backend.Rows);
        backend.Multiplexer.DidNotReceive().GetDatabase(Arg.Any<int>(), Arg.Any<object>());
        table.Dispose();
        await table.DisposeAsync();
        backend.Multiplexer.DidNotReceive().Dispose();
        await backend.Multiplexer.Received(isShared ? 0 : 1).DisposeAsync();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Initialize_DisposedDuringExpiry_PreservesDisposalAndConnectionOwnership(bool repeated, bool isShared, bool disposeAsync)
    {
        var backend = new MembershipBackend();
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            return Task.FromResult((backend.Multiplexer, isShared));
        });
        var token = TestContext.Current.CancellationToken;
        if (repeated)
        {
            await table.InitializeMembershipTableAsync(true, token);
        }

        var expiry = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Database.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>()).Returns(expiry.Task);
        var initialization = table.InitializeMembershipTableAsync(true, token);
        var waiting = table.InitializeMembershipTableAsync(true, token);
        Assert.False(initialization.IsCompleted);
        Assert.False(waiting.IsCompleted);

        Task disposal = Task.CompletedTask;
        if (disposeAsync)
        {
            disposal = table.DisposeAsync().AsTask();
        }
        else
        {
            table.Dispose();
        }

        Assert.False(table.IsInitialized);
        Assert.False(expiry.Task.IsCompleted);
        backend.Multiplexer.DidNotReceive().Dispose();
        await backend.Multiplexer.DidNotReceive().DisposeAsync();
        if (disposeAsync)
        {
            Assert.Equal(repeated && !isShared, !disposal.IsCompleted);
        }

        expiry.SetResult(true);

        var error = await Assert.ThrowsAsync<ObjectDisposedException>(() => initialization.WaitAsync(token));
        var waitingError = await Assert.ThrowsAsync<ObjectDisposedException>(() => waiting.WaitAsync(token));
        Assert.Equal(typeof(RedisMembershipTable).FullName, error.ObjectName);
        Assert.Equal(typeof(RedisMembershipTable).FullName, waitingError.ObjectName);
        Assert.False(table.IsInitialized);
        Assert.Equal(1, factoryCalls);
        await disposal.WaitAsync(token);
        await table.DisposeAsync();
        table.Dispose();
        backend.Multiplexer.DidNotReceive().Dispose();
        await backend.Multiplexer.Received(!isShared ? 1 : 0).DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WaitsForInitializingConnectionCleanup(bool disposalFails)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, false)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var versionWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), When.NotExists)
            .Returns(versionWrite.Task);
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Multiplexer.DisposeAsync().Returns(_ =>
        {
            disposalStarted.SetResult();
            return new ValueTask(connectionDisposal.Task);
        });
        backend.Database.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>()).Returns(_ =>
        {
            Assert.False(disposalStarted.Task.IsCompleted);
            return Task.FromResult(true);
        });
        var initialization = table.InitializeMembershipTableAsync(true, token);

        var firstDisposal = table.DisposeAsync().AsTask();
        var secondDisposal = table.DisposeAsync().AsTask();
        table.Dispose();

        Assert.False(initialization.IsCompleted);
        Assert.False(firstDisposal.IsCompleted);
        Assert.False(secondDisposal.IsCompleted);
        Assert.False(disposalStarted.Task.IsCompleted);
        Assert.False(table.IsInitialized);
        versionWrite.SetResult(false);
        await disposalStarted.Task.WaitAsync(token);
        Assert.False(initialization.IsCompleted);
        Assert.False(firstDisposal.IsCompleted);
        Assert.False(secondDisposal.IsCompleted);
        if (disposalFails)
        {
            var failure = new InvalidOperationException("Connection disposal failed.");
            connectionDisposal.SetException(failure);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => initialization.WaitAsync(token)));
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => firstDisposal.WaitAsync(token)));
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => secondDisposal.WaitAsync(token)));
        }
        else
        {
            connectionDisposal.SetResult();
            var error = await Assert.ThrowsAsync<ObjectDisposedException>(() => initialization.WaitAsync(token));
            Assert.Equal(typeof(RedisMembershipTable).FullName, error.ObjectName);
            await Task.WhenAll(firstDisposal, secondDisposal).WaitAsync(token);
        }

        Assert.False(table.IsInitialized);
        backend.Multiplexer.DidNotReceive().Dispose();
        _ = backend.Multiplexer.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_DisposedAndCanceledDuringFactory_PreservesCancellationAndDisposesLateConnection(bool disposeAsync)
    {
        var backend = new MembershipBackend();
        var creation = new TaskCompletionSource<(IConnectionMultiplexer, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Multiplexer.DisposeAsync().Returns(_ =>
        {
            disposed.SetResult();
            return ValueTask.CompletedTask;
        });
        using var table = CreateTable(_ => creation.Task);
        var token = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var initialization = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.False(initialization.IsCompleted);

        if (disposeAsync)
        {
            await table.DisposeAsync();
        }
        else
        {
            table.Dispose();
        }

        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization.WaitAsync(token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(table.IsInitialized);
        Assert.False(creation.Task.IsCompleted);

        creation.SetResult((backend.Multiplexer, false));
        await disposed.Task.WaitAsync(token);

        Assert.False(table.IsInitialized);
        backend.Multiplexer.DidNotReceive().GetDatabase(Arg.Any<int>(), Arg.Any<object>());
        await backend.Multiplexer.Received(1).DisposeAsync();
        backend.Multiplexer.DidNotReceive().Dispose();
    }

    [Fact]
    public async Task Dispose_OverlappingSyncAndAsyncCalls_DisposesOwnedConnectionOnce()
    {
        var backend = new MembershipBackend();
        var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Multiplexer.DisposeAsync().Returns(_ => new ValueTask(disposal.Task));
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, false)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);

        var first = table.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(table.IsInitialized);
        table.Dispose();
        await table.DisposeAsync();

        var error = await Assert.ThrowsAsync<ObjectDisposedException>(() => table.InitializeMembershipTableAsync(true, token));
        Assert.Equal(typeof(RedisMembershipTable).FullName, error.ObjectName);
        disposal.SetResult();
        await first.WaitAsync(token);

        Assert.False(table.IsInitialized);
        backend.Multiplexer.DidNotReceive().Dispose();
        await backend.Multiplexer.Received(1).DisposeAsync();
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
    public async Task ReadRow_CanceledDuringCommand_DoesNotReturnSuccess()
    {
        var execution = new TaskCompletionSource<RedisValue[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = Substitute.For<IDatabase>();
        database.HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>()).Returns(execution.Task);
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
        _ = database.Received(1).HashGetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue[]>());
        execution.SetResult(["0", RedisValue.Null]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialize_EntryExpiry_RefreshesConfiguredTtl(bool expires)
    {
        var backend = new MembershipBackend();
        using var table = new RedisMembershipTable(
            Options.Create(new RedisClusteringOptions
            {
                CreateMultiplexer = _ => Task.FromResult((backend.Multiplexer, true)),
                EntryExpiry = expires ? TimeSpan.FromHours(1) : null
            }),
            Options.Create(new ClusterOptions { ServiceId = "service", ClusterId = "cluster" }));
        var token = TestContext.Current.CancellationToken;

        await table.InitializeMembershipTableAsync(false, token);
        Assert.Empty(backend.Rows);
        await backend.Database.DidNotReceive().KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>());
        await table.InitializeMembershipTableAsync(true, token);
        backend.Rows["Version"] = "17";
        await table.InitializeMembershipTableAsync(true, token);

        Assert.Equal((RedisValue)"17", Assert.Single(backend.Rows).Value);
        await backend.Database.Received(expires ? 2 : 0).KeyExpireAsync(MembershipBackend.ClusterKey, TimeSpan.FromHours(1));
        await backend.Database.DidNotReceive().KeyPersistAsync(Arg.Any<RedisKey>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Initialize_Repeated_ReusesConnectionAndBootstrapsAfterDelete(bool isShared, bool disposeAsync)
    {
        var backend = new MembershipBackend();
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            return Task.FromResult((backend.Multiplexer, isShared));
        });
        var token = TestContext.Current.CancellationToken;

        await table.InitializeMembershipTableAsync(false, token);
        Assert.Empty(backend.Rows);
        await table.InitializeMembershipTableAsync(true, token);
        Assert.Equal((RedisValue)"0", backend.Rows["Version"]);
        backend.Rows["Version"] = "17";
        await table.InitializeMembershipTableAsync(true, token);
        Assert.Equal(17, (await table.ReadAllAsync(token)).Version.Version);
        await table.DeleteMembershipTableEntriesAsync("cluster", token);
        Assert.Empty(backend.Rows);
        await table.InitializeMembershipTableAsync(true, token);

        Assert.True(table.IsInitialized);
        Assert.Equal((RedisValue)"0", Assert.Single(backend.Rows).Value);
        Assert.Equal(1, factoryCalls);
        backend.Multiplexer.Received(1).GetDatabase(Arg.Any<int>(), Arg.Any<object>());
        await backend.Database.Received(3).HashSetAsync(MembershipBackend.ClusterKey, "Version", "0", When.NotExists);
        await backend.Database.Received(3).KeyExpireAsync(MembershipBackend.ClusterKey, TimeSpan.FromHours(1));
        backend.Multiplexer.DidNotReceive().Dispose();
        await backend.Multiplexer.DidNotReceive().DisposeAsync();

        if (disposeAsync)
        {
            await table.DisposeAsync();
        }
        else
        {
            table.Dispose();
        }

        Assert.False(table.IsInitialized);
        table.Dispose();
        backend.Multiplexer.Received(!isShared && !disposeAsync ? 1 : 0).Dispose();
        await backend.Multiplexer.Received(!isShared && disposeAsync ? 1 : 0).DisposeAsync();
    }

    [Fact]
    public async Task Initialize_OverlappingCalls_CreateOneConnectionAndRefreshEachCall()
    {
        var backend = new MembershipBackend();
        var creation = new TaskCompletionSource<(IConnectionMultiplexer, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            return creation.Task;
        });
        var token = TestContext.Current.CancellationToken;
        var first = table.InitializeMembershipTableAsync(true, token);
        var second = table.InitializeMembershipTableAsync(true, token);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, factoryCalls);
        creation.SetResult((backend.Multiplexer, false));
        await Task.WhenAll(first, second).WaitAsync(token);

        Assert.Equal(1, factoryCalls);
        await backend.Database.Received(2).HashSetAsync(MembershipBackend.ClusterKey, "Version", "0", When.NotExists);
        await backend.Database.Received(2).KeyExpireAsync(MembershipBackend.ClusterKey, TimeSpan.FromHours(1));
        Assert.Equal((RedisValue)"0", Assert.Single(backend.Rows).Value);
    }

    [Fact]
    public async Task Initialize_CanceledWhileWaiting_PreservesFirstInitialization()
    {
        var backend = new MembershipBackend();
        var creation = new TaskCompletionSource<(IConnectionMultiplexer, bool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            return creation.Task;
        });
        var token = TestContext.Current.CancellationToken;
        var first = table.InitializeMembershipTableAsync(true, token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var waiting = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(first.IsCompleted);
        Assert.Equal(1, factoryCalls);
        creation.SetResult((backend.Multiplexer, false));
        await first.WaitAsync(token);

        Assert.Equal(1, factoryCalls);
        Assert.True(table.IsInitialized);
        await backend.Database.Received(1).HashSetAsync(MembershipBackend.ClusterKey, "Version", "0", When.NotExists);
    }

    [Fact]
    public async Task Initialize_RepeatedCancellation_RetainsOwnedConnectionForRetry()
    {
        var backend = new MembershipBackend();
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            return Task.FromResult((backend.Multiplexer, false));
        });
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var versionWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), When.NotExists)
            .Returns(versionWrite.Task);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var repeated = table.InitializeMembershipTableAsync(true, cancellation.Token);
        Assert.False(repeated.IsCompleted);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repeated.WaitAsync(token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(table.IsInitialized);
        Assert.Equal(1, factoryCalls);
        Assert.False(versionWrite.Task.IsCompleted);
        backend.Multiplexer.DidNotReceive().Dispose();
        await backend.Multiplexer.DidNotReceive().DisposeAsync();
        await backend.Database.Received(1).KeyExpireAsync(MembershipBackend.ClusterKey, TimeSpan.FromHours(1));

        versionWrite.SetResult(false);
        await table.InitializeMembershipTableAsync(true, token);
        Assert.Equal(1, factoryCalls);
        await backend.Database.Received(2).KeyExpireAsync(MembershipBackend.ClusterKey, TimeSpan.FromHours(1));
        Assert.Equal(0, (await table.ReadAllAsync(token)).Version.Version);
    }

    [Fact]
    public async Task Initialize_RepeatedFailure_RetainsOwnedConnectionForRetry()
    {
        var backend = new MembershipBackend();
        var factoryCalls = 0;
        using var table = CreateTable(_ =>
        {
            factoryCalls++;
            return Task.FromResult((backend.Multiplexer, false));
        });
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var failure = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Expiry refresh failed.");
        backend.Database.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>())
            .Returns(Task.FromException<bool>(failure), Task.FromResult(true));

        Assert.Same(failure, await Assert.ThrowsAsync<RedisConnectionException>(
            () => table.InitializeMembershipTableAsync(true, token)));
        Assert.True(table.IsInitialized);
        backend.Multiplexer.DidNotReceive().Dispose();
        await backend.Multiplexer.DidNotReceive().DisposeAsync();

        await table.InitializeMembershipTableAsync(true, token);
        Assert.Equal(1, factoryCalls);
        await backend.Database.Received(3).KeyExpireAsync(MembershipBackend.ClusterKey, TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Delete_ValidatesClusterAndDeletesOnlyConfiguredKey()
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => table.DeleteMembershipTableEntriesAsync("other", token));
        Assert.Equal("clusterId", error.ParamName);
        Assert.Equal((RedisValue)"0", Assert.Single(backend.Rows).Value);
        await backend.Database.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());

        await table.DeleteMembershipTableEntriesAsync("cluster", token);
        Assert.Empty(backend.Rows);
        await backend.Database.Received(1).KeyDeleteAsync(MembershipBackend.ClusterKey);
    }

    [Fact]
    public async Task Insert_ValidatesTableTokenAndDuplicateRowAtomically()
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();

        Assert.False(await table.InsertRowAsync(entry, new TableVersion(1, "stale"), token));
        Assert.Equal((RedisValue)"0", Assert.Single(backend.Rows).Value);
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), token));
        var persisted = backend.Rows[entry.SiloAddress.ToString()];
        Assert.False(await table.InsertRowAsync(entry, new TableVersion(2, "1"), token));

        Assert.Equal(2, backend.Rows.Count);
        Assert.Equal((RedisValue)"1", backend.Rows["Version"]);
        Assert.Equal(persisted, backend.Rows[entry.SiloAddress.ToString()]);
        Assert.Equal([false, true, false], backend.Commits);
    }

    [Fact]
    public async Task MembershipWriteReceipts_ChainExactCommittedTokensWithoutReads()
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        backend.Database.ClearReceivedCalls();

        var inserted = await table.InsertRowWithResultAsync(entry, new TableVersion(1, "0"), token);

        Assert.True(inserted.Succeeded);
        var first = Assert.IsType<MembershipTableWriteReceipt>(inserted.Receipt);
        Assert.Equal(new TableVersion(1, "1"), first.Version);
        Assert.Equal("1", first.RowETag);
        Assert.Equal(first.Version.VersionEtag, backend.Rows["Version"].ToString());
        entry.Status = SiloStatus.Dead;
        var updated = await table.UpdateRowWithResultAsync(entry, first.RowETag, first.Version.Next(), token);

        Assert.True(updated.Succeeded);
        var second = Assert.IsType<MembershipTableWriteReceipt>(updated.Receipt);
        Assert.Equal(new TableVersion(2, "2"), second.Version);
        Assert.Equal("2", second.RowETag);
        Assert.Equal(second.Version.VersionEtag, backend.Rows["Version"].ToString());
        Assert.Equal(SiloStatus.Dead, backend.Read(entry).Status);
        var duplicate = await table.InsertRowWithResultAsync(entry, second.Version.Next(), token);
        Assert.False(duplicate.Succeeded);
        Assert.Null(duplicate.Receipt);
        var stale = await table.UpdateRowWithResultAsync(entry, first.RowETag, first.Version.Next(), token);
        Assert.False(stale.Succeeded);
        Assert.Null(stale.Receipt);
        var mismatched = await table.UpdateRowWithResultAsync(entry, "invalid", second.Version.Next(), token);
        Assert.False(mismatched.Succeeded);
        Assert.Null(mismatched.Receipt);
        Assert.Equal(new TableVersion(1, "1"), first.Version);
        Assert.Equal((RedisValue)"2", backend.Rows["Version"]);
        Assert.Equal([true, true, false, false], backend.Commits);
        var calls = backend.Database.ReceivedCalls().ToArray();
        Assert.Equal(4, calls.Length);
        Assert.All(calls, call => Assert.Equal(nameof(IDatabase.ScriptEvaluateAsync), call.GetMethodInfo().Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MembershipWriteReceipts_StorageErrorPropagatesWithoutReads(bool update)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var failure = new RedisServerException("NOPERM write denied");
        backend.Database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), CommandFlags.NoScriptCache)
            .Returns(Task.FromException<RedisResult>(failure));
        backend.Database.ClearReceivedCalls();
        var entry = CreateEntry();

        var operation = update
            ? table.UpdateRowWithResultAsync(entry, "0", new TableVersion(1, "0"), token)
            : table.InsertRowWithResultAsync(entry, new TableVersion(1, "0"), token);

        Assert.Same(failure, await Assert.ThrowsAsync<RedisServerException>(() => operation));
        Assert.Equal(nameof(IDatabase.ScriptEvaluateAsync), Assert.Single(backend.Database.ReceivedCalls()).GetMethodInfo().Name);
        Assert.Equal((RedisValue)"0", Assert.Single(backend.Rows).Value);
        Assert.Empty(backend.Commits);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 10)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 10)]
    public async Task CanonicalWrite_RequiresNextVersionWithCurrentToken(bool insert, int proposedVersion)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var existing = CreateEntry();
        Assert.True(await table.InsertRowAsync(existing, new TableVersion(1, "0"), token));
        var captured = backend.Rows[existing.SiloAddress.ToString()];
        var entry = CreateEntry();
        entry.Status = SiloStatus.Dead;
        if (insert)
        {
            entry.SiloAddress = SiloAddress.New(IPAddress.Loopback, 22222, 1);
        }

        var invalid = new TableVersion(proposedVersion, "1");
        var result = insert
            ? await table.InsertRowAsync(entry, invalid, token)
            : await table.UpdateRowAsync(entry, "1", invalid, token);

        Assert.False(result);
        Assert.Equal(2, backend.Rows.Count);
        Assert.Equal((RedisValue)"1", backend.Rows["Version"]);
        Assert.Equal(captured, backend.Rows[existing.SiloAddress.ToString()]);
        Assert.Equal([true, false], backend.Commits);
        var next = new TableVersion(2, "1");
        Assert.True(insert
            ? await table.InsertRowAsync(entry, next, token)
            : await table.UpdateRowAsync(entry, "1", next, token));
        Assert.Equal((RedisValue)"2", backend.Rows["Version"]);
        Assert.Equal(SiloStatus.Dead, backend.Read(entry).Status);
    }

    [Fact]
    public async Task Insert_ConcurrentWriter_AllowsOneWinner()
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        var winner = CreateEntry();
        winner.HostName = "winner";
        backend.BeforeExecute = async () => Assert.True(await table.InsertRowAsync(winner, new TableVersion(1, "0"), token));

        Assert.False(await table.InsertRowAsync(entry, new TableVersion(1, "0"), token));

        Assert.Equal(2, backend.Rows.Count);
        Assert.Equal((RedisValue)"1", backend.Rows["Version"]);
        Assert.Equal("winner", backend.Read(entry).HostName);
        Assert.Equal([true, false], backend.Commits);
    }

    [Fact]
    public async Task Insert_OtherSiloHeartbeatPreservesOriginalTableToken()
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var owner = CreateEntry();
        Assert.True(await table.InsertRowAsync(owner, new TableVersion(1, "0"), token));
        var version = (await table.ReadAllAsync(token)).Version.Next();
        var entry = CreateEntry();
        entry.SiloAddress = SiloAddress.New(IPAddress.Loopback, 22222, 1);
        owner.IAmAliveTime = owner.IAmAliveTime.AddTicks(1);
        backend.BeforeExecute = () => table.UpdateIAmAliveAsync(owner, token);
        backend.Database.ClearReceivedCalls();

        Assert.True(await table.InsertRowAsync(entry, version, token));

        Assert.Equal(owner.IAmAliveTime, backend.Read(owner).IAmAliveTime);
        Assert.Equal(entry.SiloAddress, backend.Read(entry).SiloAddress);
        Assert.Equal((RedisValue)"2", backend.Rows["Version"]);
        Assert.Equal([true, true], backend.Commits);
        var commands = backend.Database.ReceivedCalls().ToArray();
        Assert.Equal(2, commands.Length);
        Assert.All(commands, call => Assert.Equal(nameof(IDatabase.ScriptEvaluateAsync), call.GetMethodInfo().Name));
        var canonicalWrite = Assert.Single(commands, call => Assert.IsType<RedisValue[]>(call.GetArguments()[2]).Length == 5);
        Assert.Equal(new RedisValue[] { entry.SiloAddress.ToString(), "1", "2", JsonConvert.SerializeObject(entry, JsonSettings.JsonSerializerSettings), 0 },
            Assert.IsType<RedisValue[]>(canonicalWrite.GetArguments()[2]));
        backend.Database.DidNotReceive().CreateTransaction();
    }

    [Theory]
    [InlineData("stale", "1")]
    [InlineData("1", "stale")]
    [InlineData("0", "0")]
    public async Task Update_ValidatesRowAndTableTokensIndependently(string rowEtag, string tableEtag)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), token));
        var persisted = backend.Rows[entry.SiloAddress.ToString()];
        entry.Status = SiloStatus.Dead;
        backend.Database.ClearReceivedCalls();

        Assert.False(await table.UpdateRowAsync(entry, rowEtag, new TableVersion(2, tableEtag), token));

        Assert.Equal(2, backend.Rows.Count);
        Assert.Equal((RedisValue)"1", backend.Rows["Version"]);
        Assert.Equal(persisted, backend.Rows[entry.SiloAddress.ToString()]);
        Assert.Equal(rowEtag == tableEtag ? 1 : 0, backend.Database.ReceivedCalls().Count());
        Assert.All(backend.Database.ReceivedCalls(), call => Assert.Equal(nameof(IDatabase.ScriptEvaluateAsync), call.GetMethodInfo().Name));
        backend.Database.DidNotReceive().CreateTransaction();
    }

    [Fact]
    public async Task Update_MissingRow_RejectsWithoutChangingTable()
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);

        Assert.False(await table.UpdateRowAsync(CreateEntry(), "0", new TableVersion(1, "0"), token));

        Assert.Equal((RedisValue)"0", Assert.Single(backend.Rows).Value);
        backend.Database.DidNotReceive().CreateTransaction();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reads_ReturnCoherentRowAndVersion(bool readAll)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), token));
        backend.AfterRead = () =>
        {
            entry.Status = SiloStatus.Dead;
            backend.Store(entry);
            backend.Rows["Version"] = "2";
        };

        var snapshot = readAll ? await table.ReadAllAsync(token) : await table.ReadRowAsync(entry.SiloAddress, token);

        Assert.Equal(1, snapshot.Version.Version);
        Assert.Equal("1", snapshot.Version.VersionEtag);
        var row = Assert.Single(snapshot.Members);
        Assert.Equal("1", row.Item2);
        Assert.Equal(SiloStatus.Active, row.Item1.Status);
        Assert.Equal((RedisValue)"2", backend.Rows["Version"]);
        Assert.Equal(SiloStatus.Dead, backend.Read(entry).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_HeartbeatPreservesOriginalTokensAndNeedsOneCommand(bool concurrentHeartbeat)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), token));
        var snapshot = await table.ReadRowAsync(entry.SiloAddress, token);
        var rowEtag = Assert.Single(snapshot.Members).Item2;
        var nextVersion = snapshot.Version.Next();
        var heartbeat = CreateEntry();
        heartbeat.IAmAliveTime = entry.IAmAliveTime.AddTicks(10);
        if (concurrentHeartbeat)
        {
            backend.BeforeExecute = () => table.UpdateIAmAliveAsync(heartbeat, token);
        }
        else
        {
            await table.UpdateIAmAliveAsync(heartbeat, token);
        }

        entry.Status = SiloStatus.Dead;
        entry.SuspectTimes = [Tuple.Create(entry.SiloAddress, entry.IAmAliveTime.AddTicks(1))];
        var original = JsonConvert.SerializeObject(entry, JsonSettings.JsonSerializerSettings);
        backend.Database.ClearReceivedCalls();
        Assert.True(await table.UpdateRowAsync(entry, rowEtag, nextVersion, token));

        var persisted = backend.Read(entry);
        Assert.Equal(SiloStatus.Dead, persisted.Status);
        Assert.Equal(entry.SuspectTimes, persisted.SuspectTimes);
        Assert.Equal(original, JsonConvert.SerializeObject(entry, JsonSettings.JsonSerializerSettings));
        Assert.Equal((RedisValue)"2", backend.Rows["Version"]);
        Assert.Equal([true, true], backend.Commits);
        var commands = backend.Database.ReceivedCalls().ToArray();
        Assert.Equal(concurrentHeartbeat ? 2 : 1, commands.Length);
        Assert.All(commands, call =>
        {
            Assert.Equal(nameof(IDatabase.ScriptEvaluateAsync), call.GetMethodInfo().Name);
            Assert.Equal(CommandFlags.NoScriptCache, Assert.IsType<CommandFlags>(call.GetArguments()[3]));
        });
        var canonicalWrite = Assert.Single(commands, call => Assert.IsType<RedisValue[]>(call.GetArguments()[2]).Length == 5);
        Assert.Equal(new RedisValue[] { entry.SiloAddress.ToString(), rowEtag, "2", original, 1 },
            Assert.IsType<RedisValue[]>(canonicalWrite.GetArguments()[2]));
        backend.Database.DidNotReceive().CreateTransaction();
    }

    [Fact]
    public async Task Update_ConcurrentTableChange_RejectsAtomicWrite()
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), token));
        var persisted = backend.Rows[entry.SiloAddress.ToString()];
        var other = CreateEntry();
        other.SiloAddress = SiloAddress.New(IPAddress.Loopback, 22222, 1);
        backend.BeforeExecute = async () => Assert.True(await table.InsertRowAsync(other, new TableVersion(2, "1"), token));
        entry.Status = SiloStatus.Dead;

        Assert.False(await table.UpdateRowAsync(entry, "1", new TableVersion(2, "1"), token));

        Assert.Equal((RedisValue)"2", backend.Rows["Version"]);
        Assert.Equal(persisted, backend.Rows[entry.SiloAddress.ToString()]);
        Assert.Equal(other.SiloAddress, backend.Read(other).SiloAddress);
        Assert.Equal([true, true, false], backend.Commits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateIAmAlive_UsesOneWriteCommandAndOnlyChangesHeartbeat(bool hasVersion)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(hasVersion, token);
        var entry = CreateEntry();
        entry.HostName = "host,\"IAmAliveTime\":\"not-a-timestamp\"";
        entry.SiloName = "silo";
        entry.SuspectTimes = [Tuple.Create(entry.SiloAddress, entry.StartTime.AddTicks(1))];
        backend.Store(entry);
        var before = backend.Rows[entry.SiloAddress.ToString()].ToString();
        var heartbeat = new MembershipEntry
        {
            SiloAddress = entry.SiloAddress,
            IAmAliveTime = entry.IAmAliveTime.AddTicks(1234567)
        };
        backend.Database.ClearReceivedCalls();

        await table.UpdateIAmAliveAsync(heartbeat, token);

        var timestamp = JsonConvert.SerializeObject(heartbeat.IAmAliveTime, JsonSettings.JsonSerializerSettings);
        var command = Assert.Single(backend.Database.ReceivedCalls());
        Assert.Equal(nameof(IDatabase.ScriptEvaluateAsync), command.GetMethodInfo().Name);
        var arguments = command.GetArguments();
        Assert.Equal(MembershipBackend.ClusterKey, Assert.Single(Assert.IsType<RedisKey[]>(arguments[1])));
        Assert.Equal(new RedisValue[] { entry.SiloAddress.ToString(), timestamp }, Assert.IsType<RedisValue[]>(arguments[2]));
        Assert.Equal(CommandFlags.NoScriptCache, Assert.IsType<CommandFlags>(arguments[3]));
        var expected = before.Replace(
            "\"IAmAliveTime\":" + JsonConvert.SerializeObject(entry.IAmAliveTime, JsonSettings.JsonSerializerSettings),
            "\"IAmAliveTime\":" + timestamp, StringComparison.Ordinal);
        Assert.NotEqual(before, expected);
        Assert.Equal(expected, backend.Rows[entry.SiloAddress.ToString()].ToString());
        Assert.Equal(heartbeat.IAmAliveTime, backend.Read(entry).IAmAliveTime);
        Assert.Equal(hasVersion, backend.Rows.ContainsKey("Version"));
        if (hasVersion)
        {
            Assert.Equal((RedisValue)"0", backend.Rows["Version"]);
        }

        backend.Database.DidNotReceive().CreateTransaction();
    }

    [Theory]
    [InlineData("ReadAll")]
    [InlineData("ReadRow")]
    [InlineData("Cleanup")]
    public async Task Operations_LostHistory_ReportsMissingVersion(string operation)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        Assert.True(await table.InsertRowAsync(entry, new TableVersion(1, "0"), token));
        backend.Rows.Clear();

        var error = await Assert.ThrowsAsync<RedisClusteringException>(() => operation switch
        {
            "ReadAll" => table.ReadAllAsync(token),
            "ReadRow" => table.ReadRowAsync(entry.SiloAddress, token),
            "Cleanup" => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        Assert.Contains("version is missing", error.Message);
        Assert.Empty(backend.Rows);
        Assert.Single(backend.Commits);
        backend.Database.DidNotReceive().CreateTransaction();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalWrite_LostTable_RejectsExpectedToken(bool insert)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        backend.Rows.Clear();

        var result = insert
            ? await table.InsertRowAsync(entry, new TableVersion(1, "0"), token)
            : await table.UpdateRowAsync(entry, "0", new TableVersion(1, "0"), token);

        Assert.False(result);
        Assert.Empty(backend.Rows);
        Assert.Equal([false], backend.Commits);
        backend.Database.DidNotReceive().CreateTransaction();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("invalid")]
    public async Task ReadAll_InvalidVersion_ReportsError(string version)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        backend.Rows["Version"] = version;

        if (version.Length == 0)
        {
            var error = await Assert.ThrowsAsync<RedisClusteringException>(() => table.ReadAllAsync(token));
            Assert.Contains("version is missing", error.Message);
        }
        else
        {
            await Assert.ThrowsAsync<FormatException>(() => table.ReadAllAsync(token));
        }

        Assert.Equal((RedisValue)version, Assert.Single(backend.Rows).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateIAmAlive_InfrastructureFailure_PropagatesSameException(bool permissions)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        backend.Store(entry);
        var persisted = backend.Rows[entry.SiloAddress.ToString()];
        Exception failure = permissions
            ? new RedisServerException("NOPERM this user has no permissions to access the key")
            : new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Connection unavailable.");
        backend.Database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), CommandFlags.NoScriptCache)
            .Returns(Task.FromException<RedisResult>(failure));
        backend.Database.ClearReceivedCalls();

        entry.IAmAliveTime = entry.IAmAliveTime.AddTicks(1);
        Assert.Same(failure, await Record.ExceptionAsync(() => table.UpdateIAmAliveAsync(entry, token)));

        Assert.Equal(2, backend.Rows.Count);
        Assert.Equal((RedisValue)"0", backend.Rows["Version"]);
        Assert.Equal(persisted, backend.Rows[entry.SiloAddress.ToString()]);
        Assert.Equal(nameof(IDatabase.ScriptEvaluateAsync), Assert.Single(backend.Database.ReceivedCalls()).GetMethodInfo().Name);
        backend.Database.DidNotReceive().CreateTransaction();
        Assert.Empty(backend.Commits);
    }

    [Fact]
    public async Task UpdateIAmAlive_CanceledDuringCommand_PreservesCancellation()
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var execution = new TaskCompletionSource<RedisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), CommandFlags.NoScriptCache)
            .Returns(execution.Task);
        backend.Database.ClearReceivedCalls();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

        var pending = table.UpdateIAmAliveAsync(CreateEntry(), cancellation.Token);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(execution.Task.IsCompleted);
        Assert.Equal(nameof(IDatabase.ScriptEvaluateAsync), Assert.Single(backend.Database.ReceivedCalls()).GetMethodInfo().Name);
        backend.Database.DidNotReceive().CreateTransaction();
        execution.SetResult(RedisResult.Create(0));
    }

    [Theory]
    [InlineData("Insert")]
    [InlineData("Update")]
    [InlineData("InsertWithResult")]
    [InlineData("UpdateWithResult")]
    [InlineData("Cleanup")]
    public async Task Writes_CanceledDuringCommand_ReturnCancellation(string operation)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        entry.Status = SiloStatus.Dead;
        if (operation is not ("Insert" or "InsertWithResult"))
        {
            backend.Store(entry);
        }

        entry.IAmAliveTime = entry.IAmAliveTime.AddTicks(1);
        var execution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeExecute = () => execution.Task;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = operation switch
        {
            "Insert" => table.InsertRowAsync(entry, new TableVersion(1, "0"), cancellation.Token),
            "Update" => table.UpdateRowAsync(entry, "0", new TableVersion(1, "0"), cancellation.Token),
            "InsertWithResult" => table.InsertRowWithResultAsync(entry, new TableVersion(1, "0"), cancellation.Token),
            "UpdateWithResult" => table.UpdateRowWithResultAsync(entry, "0", new TableVersion(1, "0"), cancellation.Token),
            "Cleanup" => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Empty(backend.Commits);
        execution.SetResult();
        await backend.Executions.Single().WaitAsync(token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_CompletedCommand_ReturnsSuccessAfterCancellation(bool withResult)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        backend.Store(entry);
        var competitor = CreateEntry();
        competitor.IAmAliveTime = competitor.IAmAliveTime.AddTicks(1);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        backend.BeforeExecute = async () =>
        {
            await table.UpdateIAmAliveAsync(competitor, token);
            cancellation.Cancel();
        };
        entry.IAmAliveTime = entry.IAmAliveTime.AddTicks(2);
        entry.Status = SiloStatus.Dead;

        if (withResult)
        {
            var result = await table.UpdateRowWithResultAsync(entry, "0", new TableVersion(1, "0"), cancellation.Token);
            Assert.True(result.Succeeded);
            var receipt = Assert.IsType<MembershipTableWriteReceipt>(result.Receipt);
            Assert.Equal(new TableVersion(1, "1"), receipt.Version);
            Assert.Equal("1", receipt.RowETag);
        }
        else
        {
            Assert.True(await table.UpdateRowAsync(entry, "0", new TableVersion(1, "0"), cancellation.Token));
        }
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal([true], backend.Commits);
        Assert.Equal((RedisValue)"1", backend.Rows["Version"]);
        Assert.Equal(SiloStatus.Dead, backend.Read(entry).Status);
        Assert.Equal(entry.IAmAliveTime, backend.Read(entry).IAmAliveTime);
    }

    [Theory]
    [InlineData(SiloStatus.Created)]
    [InlineData(SiloStatus.Joining)]
    [InlineData(SiloStatus.Active)]
    [InlineData(SiloStatus.ShuttingDown)]
    [InlineData(SiloStatus.Stopping)]
    [InlineData(SiloStatus.Dead)]
    public async Task Cleanup_DeletesOnlyDeadRowsWithoutChangingVersion(SiloStatus status)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        backend.Rows["Version"] = int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var entry = CreateEntry();
        entry.Status = status;
        backend.Store(entry);
        var persisted = backend.Rows[entry.SiloAddress.ToString()];

        await table.CleanupDefunctSiloEntriesAsync(entry.IAmAliveTime.AddTicks(1), token);

        Assert.Equal((RedisValue)"2147483647", backend.Rows["Version"]);
        if (status == SiloStatus.Dead)
        {
            Assert.Single(backend.Rows);
            Assert.Equal([true], backend.Commits);
            backend.Database.DidNotReceive().CreateTransaction();
        }
        else
        {
            Assert.Equal(2, backend.Rows.Count);
            Assert.Equal(persisted, backend.Rows[entry.SiloAddress.ToString()]);
            await backend.Database.DidNotReceive().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), CommandFlags.NoScriptCache);
        }
    }

    [Theory]
    [InlineData("Start", -1)]
    [InlineData("Start", 0)]
    [InlineData("Start", 1)]
    [InlineData("Heartbeat", -1)]
    [InlineData("Heartbeat", 0)]
    [InlineData("Heartbeat", 1)]
    [InlineData("Vote", -1)]
    [InlineData("Vote", 0)]
    [InlineData("Vote", 1)]
    public async Task Cleanup_UsesPreciseLatestStartHeartbeatOrVote(string latestField, int offsetTicks)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        entry.Status = SiloStatus.Dead;
        var cutoff = new DateTimeOffset(entry.IAmAliveTime.AddSeconds(1)).ToOffset(TimeSpan.FromHours(3));
        var latest = cutoff.UtcDateTime.AddTicks(offsetTicks);
        switch (latestField)
        {
            case "Start": entry.StartTime = latest; break;
            case "Heartbeat": entry.IAmAliveTime = latest; break;
            case "Vote": entry.SuspectTimes = [Tuple.Create(entry.SiloAddress, latest), Tuple.Create(entry.SiloAddress, entry.StartTime)]; break;
        }

        backend.Store(entry);
        var persisted = backend.Rows[entry.SiloAddress.ToString()];
        await table.CleanupDefunctSiloEntriesAsync(cutoff, token);

        Assert.Equal(offsetTicks >= 0, backend.Rows.ContainsKey(entry.SiloAddress.ToString()));
        Assert.Equal((RedisValue)"0", backend.Rows["Version"]);
        if (offsetTicks >= 0)
        {
            Assert.Equal(persisted, backend.Rows[entry.SiloAddress.ToString()]);
            await backend.Database.DidNotReceive().ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), CommandFlags.NoScriptCache);
        }
        else
        {
            Assert.Equal([true], backend.Commits);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_ConcurrentHeartbeatOrVote_PreservesChangedRow(bool updateVote)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        entry.Status = SiloStatus.Dead;
        backend.Store(entry);
        var cutoff = entry.IAmAliveTime.AddTicks(1);
        backend.BeforeExecute = async () =>
        {
            if (updateVote)
            {
                entry.SuspectTimes = [Tuple.Create(entry.SiloAddress, cutoff)];
                Assert.True(await table.UpdateRowAsync(entry, "0", new TableVersion(1, "0"), token));
            }
            else
            {
                entry.IAmAliveTime = cutoff;
                await table.UpdateIAmAliveAsync(entry, token);
            }
        };

        await table.CleanupDefunctSiloEntriesAsync(cutoff, token);

        Assert.Equal(JsonConvert.SerializeObject(entry, JsonSettings.JsonSerializerSettings), backend.Rows[entry.SiloAddress.ToString()].ToString());
        Assert.Equal((RedisValue)(updateVote ? "1" : "0"), backend.Rows["Version"]);
        Assert.Equal(updateVote ? [true, false] : [false], backend.Commits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_UnrelatedWrite_UsesOneScanAndOneCommandPerCandidate(bool canonicalWrite)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var first = CreateEntry();
        first.Status = SiloStatus.Dead;
        var second = CreateEntry();
        second.SiloAddress = SiloAddress.New(IPAddress.Loopback, 22222, 1);
        second.Status = SiloStatus.Dead;
        var survivor = CreateEntry();
        survivor.SiloAddress = SiloAddress.New(IPAddress.Loopback, 33333, 1);
        backend.Store(first);
        backend.Store(second);
        backend.Store(survivor);
        backend.BeforeExecute = async () =>
        {
            survivor.IAmAliveTime = survivor.IAmAliveTime.AddTicks(1);
            if (canonicalWrite)
            {
                survivor.SuspectTimes = [Tuple.Create(survivor.SiloAddress, survivor.IAmAliveTime)];
                Assert.True(await table.UpdateRowAsync(survivor, "0", new TableVersion(1, "0"), token));
            }
            else
            {
                await table.UpdateIAmAliveAsync(survivor, token);
            }
        };
        backend.Database.ClearReceivedCalls();

        await table.CleanupDefunctSiloEntriesAsync(first.IAmAliveTime.AddTicks(1), token);

        Assert.Equal(2, backend.Rows.Count);
        Assert.Equal(JsonConvert.SerializeObject(survivor, JsonSettings.JsonSerializerSettings),
            backend.Rows[survivor.SiloAddress.ToString()].ToString());
        Assert.Equal((RedisValue)(canonicalWrite ? "1" : "0"), backend.Rows["Version"]);
        var calls = backend.Database.ReceivedCalls().ToArray();
        Assert.Equal(4, calls.Length);
        Assert.Equal(nameof(IDatabase.HashGetAllAsync), calls[0].GetMethodInfo().Name);
        Assert.All(calls.Skip(1), call => Assert.Equal(nameof(IDatabase.ScriptEvaluateAsync), call.GetMethodInfo().Name));
        var deletes = calls.Skip(1).Where(call => Assert.IsType<string>(call.GetArguments()[0]).Contains("'HDEL'", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, deletes.Length);
        Assert.All(deletes, call =>
        {
            Assert.Equal(CommandFlags.NoScriptCache, Assert.IsType<CommandFlags>(call.GetArguments()[3]));
            var values = Assert.IsType<RedisValue[]>(call.GetArguments()[2]);
            Assert.Equal(2, values.Length);
            var candidate = values[0] == first.SiloAddress.ToString() ? first : second;
            Assert.Equal((RedisValue)candidate.SiloAddress.ToString(), values[0]);
            Assert.Equal((RedisValue)JsonConvert.SerializeObject(candidate, JsonSettings.JsonSerializerSettings), values[1]);
        });
        backend.Database.DidNotReceive().CreateTransaction();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_ChangedOrRemovedCandidate_PreservesOtherProgress(bool removed)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var first = CreateEntry();
        first.Status = SiloStatus.Dead;
        var second = CreateEntry();
        second.SiloAddress = SiloAddress.New(IPAddress.Loopback, 22222, 1);
        second.Status = SiloStatus.Dead;
        backend.Store(first);
        backend.Store(second);
        backend.BeforeExecute = async () =>
        {
            if (removed)
            {
                backend.Rows.Remove(first.SiloAddress.ToString());
            }
            else
            {
                first.Status = SiloStatus.Active;
                Assert.True(await table.UpdateRowAsync(first, "0", new TableVersion(1, "0"), token));
            }
        };
        backend.Database.ClearReceivedCalls();

        await table.CleanupDefunctSiloEntriesAsync(first.IAmAliveTime.AddTicks(1), token);

        Assert.False(backend.Rows.ContainsKey(second.SiloAddress.ToString()));
        Assert.Equal(!removed, backend.Rows.ContainsKey(first.SiloAddress.ToString()));
        if (!removed)
        {
            Assert.Equal(SiloStatus.Active, backend.Read(first).Status);
        }

        Assert.Equal((RedisValue)(removed ? "0" : "1"), backend.Rows["Version"]);
        Assert.Equal(removed ? [false, true] : [true, false, true], backend.Commits);
        await backend.Database.Received(1).HashGetAllAsync(MembershipBackend.ClusterKey);
        await backend.Database.Received(2).ScriptEvaluateAsync(
            Arg.Is<string>(script => script.Contains("'HDEL'", StringComparison.Ordinal)),
            Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), CommandFlags.NoScriptCache);
        backend.Database.DidNotReceive().CreateTransaction();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_NativeDeleteFailure_PropagatesSameException(bool permissions)
    {
        var backend = new MembershipBackend();
        using var table = CreateTable(_ => Task.FromResult((backend.Multiplexer, true)));
        var token = TestContext.Current.CancellationToken;
        await table.InitializeMembershipTableAsync(true, token);
        var entry = CreateEntry();
        entry.Status = SiloStatus.Dead;
        backend.Store(entry);
        var original = backend.Rows[entry.SiloAddress.ToString()];
        Exception failure = permissions
            ? new RedisServerException("NOPERM this user has no permissions to run the command")
            : new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Connection unavailable.");
        backend.Database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), CommandFlags.NoScriptCache)
            .Returns(Task.FromException<RedisResult>(failure));
        backend.Database.ClearReceivedCalls();

        Assert.Same(failure, await Record.ExceptionAsync(() => table.CleanupDefunctSiloEntriesAsync(DateTimeOffset.MaxValue, token)));

        Assert.Equal(2, backend.Rows.Count);
        Assert.Equal((RedisValue)"0", backend.Rows["Version"]);
        Assert.Equal(original, backend.Rows[entry.SiloAddress.ToString()]);
        Assert.Empty(backend.Commits);
        Assert.Equal(new[] { nameof(IDatabase.HashGetAllAsync), nameof(IDatabase.ScriptEvaluateAsync) },
            backend.Database.ReceivedCalls().Select(call => call.GetMethodInfo().Name));
    }

    private static MembershipEntry CreateEntry() => new()
    {
        SiloAddress = SiloAddress.New(IPAddress.Loopback, 11111, 1),
        Status = SiloStatus.Active,
        HostName = "host",
        StartTime = DateTime.UnixEpoch,
        IAmAliveTime = DateTime.UnixEpoch.AddSeconds(1)
    };

    private static RedisMembershipTable CreateTable(Func<RedisClusteringOptions, Task<(IConnectionMultiplexer, bool)>> factory) =>
        new(
            Options.Create(new RedisClusteringOptions
            {
                CreateMultiplexer = factory,
                EntryExpiry = TimeSpan.FromHours(1)
            }),
            Options.Create(new ClusterOptions { ServiceId = "service", ClusterId = "cluster" }));

    private sealed class MembershipBackend
    {
        public static readonly RedisKey ClusterKey = RedisClusteringOptions.DefaultCreateRedisKey(new ClusterOptions { ServiceId = "service", ClusterId = "cluster" });
        public Dictionary<RedisValue, RedisValue> Rows { get; } = [];
        public IDatabase Database { get; } = Substitute.For<IDatabase>();
        public IConnectionMultiplexer Multiplexer { get; } = Substitute.For<IConnectionMultiplexer>();
        public List<Task> Executions { get; } = [];
        public List<bool> Commits { get; } = [];
        public Func<Task>? BeforeExecute { get; set; }
        public Action? AfterRead { get; set; }

        public MembershipBackend()
        {
            Database.HashSetAsync(ClusterKey, Arg.Any<RedisValue>(), Arg.Any<RedisValue>(), When.NotExists)
                .Returns(call => Task.FromResult(Rows.TryAdd(call.ArgAt<RedisValue>(1), call.ArgAt<RedisValue>(2))));
            Database.KeyExpireAsync(ClusterKey, Arg.Any<TimeSpan?>()).Returns(Task.FromResult(true));
            Database.KeyDeleteAsync(ClusterKey).Returns(_ =>
            {
                var existed = Rows.Count > 0;
                Rows.Clear();
                return Task.FromResult(existed);
            });
            Database.HashGetAsync(ClusterKey, Arg.Any<RedisValue[]>()).Returns(call =>
            {
                var result = call.ArgAt<RedisValue[]>(1).Select(key => Rows.GetValueOrDefault(key, RedisValue.Null)).ToArray();
                var afterRead = AfterRead;
                AfterRead = null;
                afterRead?.Invoke();
                return Task.FromResult(result);
            });
            Database.HashGetAllAsync(ClusterKey).Returns(_ =>
            {
                var result = Rows.Select(pair => new HashEntry(pair.Key, pair.Value)).ToArray();
                var afterRead = AfterRead;
                AfterRead = null;
                afterRead?.Invoke();
                return Task.FromResult(result);
            });
            Database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0] == ClusterKey),
                Arg.Any<RedisValue[]>(), CommandFlags.NoScriptCache).Returns(call =>
            {
                var values = call.ArgAt<RedisValue[]>(2);
                var execution = ExecuteScript(call.ArgAt<string>(0), values);
                Executions.Add(execution);
                return execution;
            });
            Multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(Database);
            Multiplexer.DisposeAsync().Returns(ValueTask.CompletedTask);
        }

        public void Store(MembershipEntry entry) => Rows[entry.SiloAddress.ToString()] = JsonConvert.SerializeObject(entry, JsonSettings.JsonSerializerSettings);

        public MembershipEntry Read(MembershipEntry entry) => JsonConvert.DeserializeObject<MembershipEntry>(Rows[entry.SiloAddress.ToString()].ToString(), JsonSettings.JsonSerializerSettings)!;

        private async Task<RedisResult> ExecuteScript(string script, RedisValue[] values)
        {
            var isDelete = script.Contains("'HDEL'", StringComparison.Ordinal);
            if (values.Length == 2 && !isDelete)
            {
                var row = Rows[values[0]].ToString();
                var entry = JsonConvert.DeserializeObject<MembershipEntry>(row, JsonSettings.JsonSerializerSettings)!;
                Rows[values[0]] = row.Replace(
                    "\"IAmAliveTime\":" + JsonConvert.SerializeObject(entry.IAmAliveTime, JsonSettings.JsonSerializerSettings),
                    "\"IAmAliveTime\":" + values[1], StringComparison.Ordinal);
                return RedisResult.Create(0);
            }

            var beforeExecute = BeforeExecute;
            BeforeExecute = null;
            if (beforeExecute is not null)
            {
                await beforeExecute();
            }

            if (isDelete)
            {
                Assert.Equal(2, values.Length);
                var deleted = Rows.GetValueOrDefault(values[0], RedisValue.Null) == values[1]
                    && Rows.Remove(values[0]);
                Commits.Add(deleted);
                return RedisResult.Create(deleted ? 1 : 0);
            }

            Assert.Equal(5, values.Length);
            var success = Rows.GetValueOrDefault("Version", RedisValue.Null) == values[1]
                && (long)values[1] == (long)values[2] - 1
                && Rows.ContainsKey(values[0]) == (values[4] == "1");
            if (success)
            {
                Rows["Version"] = values[2];
                Rows[values[0]] = values[3];
            }

            Commits.Add(success);
            return RedisResult.Create(success ? 1 : 0);
        }
    }
}
