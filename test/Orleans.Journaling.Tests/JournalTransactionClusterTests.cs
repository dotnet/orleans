using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Verifies how transactions over a grain's journaled state are scheduled: they run in the calling code's flow, within an
/// exclusive section on the activation's scheduler; while one runs, only interleaving requests start.
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class JournalTransactionClusterTests(IntegrationTestFixture fixture) : IClassFixture<IntegrationTestFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private IClusterClient Client => fixture.Client;

    [Fact]
    public async Task NonReentrantGrainMethodAwaitsATransactionInItsOwnRequest()
    {
        var grain = Client.GetGrain<ITransactionalGrain>(Guid.NewGuid());

        var result = await grain.RunTransaction("a", 1).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        Assert.Equal(1, await grain.Get("a"));
    }

    [Fact]
    public async Task ReentrantGrainMethodAwaitsATransactionInItsOwnRequest()
    {
        var grain = Client.GetGrain<IReentrantTransactionalGrain>(Guid.NewGuid());

        var result = await grain.RunTransaction("a", 1).WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        Assert.Equal(1, await grain.Get("a"));
    }

    [Fact]
    public async Task AbortedTransactionRollsBackInTheGrain()
    {
        var grain = Client.GetGrain<ITransactionalGrain>(Guid.NewGuid());
        await grain.RunTransaction("a", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.RunFailingTransaction("a", 2));

        Assert.Equal(1, await grain.Get("a"));
        await grain.Deactivate();
        Assert.Equal(1, await grain.Get("a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransactionExcludesOtherRequestsButAlwaysInterleaveOnes(bool reentrant)
    {
        ITransactionalGrain grain = reentrant
            ? Client.GetGrain<IReentrantTransactionalGrain>(Guid.NewGuid())
            : Client.GetGrain<ITransactionalGrain>(Guid.NewGuid());

        var transaction = grain.RunBlockingTransaction("a", 5);
        await WaitUntilAsync(() => grain.IsTransactionBlocked());

        // Interleaving requests read the transaction's uncommitted change, and can't change its state.
        Assert.Equal(5, await grain.ReadInterleaved("a"));
        Assert.False(await grain.TryWriteInterleaved("a", 7));

        var call = grain.Get("a");
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(call.IsCompleted);

        await grain.ReleaseTransaction();
        await transaction.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(5, await call.WaitAsync(Timeout, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentTransactionsOnAReentrantGrainRunOneAtATime()
    {
        var grain = Client.GetGrain<IReentrantTransactionalGrain>(Guid.NewGuid());

        var first = grain.RunBlockingTransaction("a", 1);
        await WaitUntilAsync(() => grain.IsTransactionBlocked());
        var second = grain.RunTransaction("b", 2);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        await grain.ReleaseTransaction();
        await first.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(2, await second.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(1, await grain.Get("a"));
    }

    [Fact]
    public async Task TransactionFromOutsideTheSchedulerThrows()
    {
        var grain = Client.GetGrain<IReentrantTransactionalGrain>(Guid.NewGuid());

        Assert.True(await grain.RunTransactionOffScheduler());
        Assert.Equal(1, await grain.RunTransaction("a", 1));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (!await condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }
}

public interface ITransactionalGrain : IGrainWithGuidKey
{
    Task<int> RunTransaction(string key, int value);

    Task RunFailingTransaction(string key, int value);

    Task RunBlockingTransaction(string key, int value);

    Task<bool> RunTransactionOffScheduler();

    [AlwaysInterleave]
    Task<bool> IsTransactionBlocked();

    [AlwaysInterleave]
    Task ReleaseTransaction();

    [AlwaysInterleave]
    Task<int> ReadInterleaved(string key);

    [AlwaysInterleave]
    Task<bool> TryWriteInterleaved(string key, int value);

    Task<int> Get(string key);

    Task Deactivate();
}

public interface IReentrantTransactionalGrain : ITransactionalGrain;

public class TransactionalGrain([FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary) : DurableGrain, ITransactionalGrain
{
    private TaskCompletionSource? _gate;
    private bool _blocked;

    private JournaledStateManager Manager => (JournaledStateManager)StateManager;

    public async Task<int> RunTransaction(string key, int value)
    {
        var result = await Manager.RunTransactionAsync(async cancellationToken =>
        {
            dictionary[key] = value;
            await Task.Delay(10, cancellationToken);
            return dictionary[key];
        });

        await WriteStateAsync();
        return result;
    }

    public async Task RunFailingTransaction(string key, int value)
    {
        await Manager.RunTransactionAsync(async _ =>
        {
            dictionary[key] = value;
            await Task.Yield();
            throw new InvalidOperationException("abort");
        });
    }

    public async Task RunBlockingTransaction(string key, int value)
    {
        await Manager.RunTransactionAsync(_ => BlockAsync(key, value));
        await WriteStateAsync();
    }

    public async Task<bool> RunTransactionOffScheduler()
    {
        try
        {
            await Task.Run(() => Manager.RunTransactionAsync(_ => Task.CompletedTask));
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public Task<bool> IsTransactionBlocked() => Task.FromResult(_blocked);

    public Task ReleaseTransaction()
    {
        _blocked = false;
        _gate!.SetResult();
        return Task.CompletedTask;
    }

    public Task<int> ReadInterleaved(string key) => Task.FromResult(dictionary.TryGetValue(key, out var value) ? value : -1);

    public Task<bool> TryWriteInterleaved(string key, int value)
    {
        try
        {
            dictionary[key] = value;
            return Task.FromResult(true);
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(false);
        }
    }

    public Task<int> Get(string key) => Task.FromResult(dictionary.TryGetValue(key, out var value) ? value : -1);

    public Task Deactivate()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    private async Task BlockAsync(string key, int value)
    {
        _gate ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        dictionary[key] = value;
        _blocked = true;
        await _gate.Task;
        _gate = null;
    }
}

[Reentrant]
public class ReentrantTransactionalGrain([FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary)
    : TransactionalGrain(dictionary), IReentrantTransactionalGrain;
