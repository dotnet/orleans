using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Verifies how transactions over a grain's journaled state are scheduled: they run on the activation's scheduler, in the
/// caller's flow, within an exclusive section; while one runs, only interleaving requests start.
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
    public async Task CommittedAndAbortedTransactionsSurviveDeactivationAsTheyEnded()
    {
        var grain = Client.GetGrain<ITransactionalGrain>(Guid.NewGuid());
        await grain.RunTransaction("a", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.RunFailingTransaction("a", 2));

        Assert.Equal(1, await grain.Get("a"));

        // The committed transaction was durable when its call returned.
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
        await grain.WaitUntilTransactionBlocked().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // Interleaving requests read the transaction's uncommitted change, and can't change its state.
        Assert.Equal(5, await grain.ReadInterleaved("a"));
        Assert.False(await grain.TryWriteInterleaved("a", 7));

        var call = grain.Get("a");
        Assert.Equal(5, await grain.ReadInterleaved("a"));
        await grain.ReleaseTransaction();
        await transaction.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(5, await call.WaitAsync(Timeout, TestContext.Current.CancellationToken));

        // The request started after the transaction ended.
        var events = await grain.GetEvents();
        Assert.True(Array.IndexOf(events, "end:a") < Array.IndexOf(events, "get:a"), string.Join(", ", events));
    }

    [Fact]
    public async Task ConcurrentTransactionsOnAReentrantGrainRunOneAtATime()
    {
        var grain = Client.GetGrain<IReentrantTransactionalGrain>(Guid.NewGuid());

        var first = grain.RunBlockingTransaction("a", 1);
        await grain.WaitUntilTransactionBlocked().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        var second = grain.RunTransaction("b", 2);
        Assert.Equal(1, await grain.ReadInterleaved("a"));

        await grain.ReleaseTransaction();
        await first.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(2, await second.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(1, await grain.Get("a"));

        var events = await grain.GetEvents();
        Assert.True(Array.IndexOf(events, "end:a") < Array.IndexOf(events, "start:b"), string.Join(", ", events));
    }

    [Fact]
    public async Task TransactionAdmitsCallbacksOnItsCallChain()
    {
        var grain = Client.GetGrain<ITransactionalGrain>(Guid.NewGuid());

        // The body calls another grain, which calls back into this one with a request which doesn't interleave. The callback
        // is on the call chain which the grain method allowed, so it runs inside the transaction's section and reads the
        // transaction's uncommitted change.
        Assert.Equal(3, await grain.RunTransactionWithCallback("a", 3).WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal(3, await grain.Get("a"));
    }

    [Fact]
    public async Task TransactionFromOutsideTheSchedulerThrows()
    {
        var grain = Client.GetGrain<IReentrantTransactionalGrain>(Guid.NewGuid());

        Assert.True(await grain.RunTransactionOffScheduler());
        Assert.Equal(1, await grain.RunTransaction("a", 1));
    }

    [Fact]
    public async Task CanceledTransactionLeavesTheQueueOfTransactions()
    {
        var grain = Client.GetGrain<IReentrantTransactionalGrain>(Guid.NewGuid());
        var first = grain.RunBlockingTransaction("a", 1);
        await grain.WaitUntilTransactionBlocked().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.True(await grain.CancelQueuedTransaction().WaitAsync(Timeout, TestContext.Current.CancellationToken));

        await grain.ReleaseTransaction();
        await first.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal(2, await grain.RunTransaction("b", 2).WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("start:canceled", await grain.GetEvents());
    }

    [Fact]
    public async Task DeactivationFaultsQueuedTransactions()
    {
        var key = Guid.NewGuid();
        var grain = Client.GetGrain<IReentrantTransactionalGrain>(key);
        await grain.StartBackgroundTransactions();
        await grain.WaitUntilTransactionBlocked().WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // The open section's code isn't a request, so deactivation doesn't wait for it, and the queued one can't run.
        await grain.Deactivate();
        var (holder, queued, gate) = TransactionalGrain.Background[key];
        await Assert.ThrowsAsync<InvalidOperationException>(() => queued.WaitAsync(Timeout, TestContext.Current.CancellationToken));

        gate.TrySetResult();
        _ = holder.ContinueWith(static task => task.Exception, TaskScheduler.Default);
    }
}

public interface ITransactionalGrain : IGrainWithGuidKey
{
    Task<int> RunTransaction(string key, int value);

    Task RunFailingTransaction(string key, int value);

    Task RunBlockingTransaction(string key, int value);

    Task<bool> RunTransactionOffScheduler();

    Task<int> RunTransactionWithCallback(string key, int value);

    Task StartBackgroundTransactions();

    [AlwaysInterleave]
    Task<bool> CancelQueuedTransaction();

    [AlwaysInterleave]
    Task WaitUntilTransactionBlocked();

    [AlwaysInterleave]
    Task ReleaseTransaction();

    [AlwaysInterleave]
    Task<int> ReadInterleaved(string key);

    [AlwaysInterleave]
    Task<bool> TryWriteInterleaved(string key, int value);

    [AlwaysInterleave]
    Task<string[]> GetEvents();

    Task<int> Get(string key);

    [AlwaysInterleave]
    Task Deactivate();
}

public interface IReentrantTransactionalGrain : ITransactionalGrain;

public interface ITransactionCallbackGrain : IGrainWithGuidKey
{
    Task<int> CallBack(ITransactionalGrain grain, string key);
}

public class TransactionCallbackGrain : Grain, ITransactionCallbackGrain
{
    public Task<int> CallBack(ITransactionalGrain grain, string key) => grain.Get(key);
}

public class TransactionalGrain([FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary) : DurableGrain, ITransactionalGrain
{
    // The transactions which StartBackgroundTransactions started, by grain, which outlive the activation.
    internal static readonly ConcurrentDictionary<Guid, (Task Holder, Task Queued, TaskCompletionSource Gate)> Background = new();

    private readonly List<string> _events = [];
    private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private JournaledStateManager Manager => (JournaledStateManager)StateManager;

    public async Task<int> RunTransaction(string key, int value)
    {
        return await Manager.RunTransactionAsync(async cancellationToken =>
        {
            _events.Add($"start:{key}");
            dictionary[key] = value;
            await Task.Delay(10, cancellationToken);
            _events.Add($"end:{key}");
            return dictionary[key];
        });
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

    public async Task RunBlockingTransaction(string key, int value) => await Manager.RunTransactionAsync(_ => BlockAsync(key, value));

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

    public async Task<int> RunTransactionWithCallback(string key, int value)
    {
        // The section which the transaction opens carries the call chain which the method allows.
        using var reentrancy = RequestContext.AllowCallChainReentrancy();
        var self = this.AsReference<ITransactionalGrain>();
        return await Manager.RunTransactionAsync(async _ =>
        {
            dictionary[key] = value;
            return await GrainFactory.GetGrain<ITransactionCallbackGrain>(Guid.NewGuid()).CallBack(self, key);
        });
    }

    public Task StartBackgroundTransactions()
    {
        // Work which the activation's scheduler runs outside any request, as a timer's or a durable task's.
        var scheduler = TaskScheduler.Current;
        var holder = Task.Factory.StartNew(() => Manager.RunTransactionAsync(_ => BlockAsync("a", 1)), CancellationToken.None, TaskCreationOptions.None, scheduler).Unwrap();
        var queued = Task.Factory.StartNew(() => Manager.RunTransactionAsync(_ => Task.CompletedTask), CancellationToken.None, TaskCreationOptions.None, scheduler).Unwrap();
        Background[this.GetPrimaryKey()] = (holder, queued, _gate);
        return Task.CompletedTask;
    }

    public async Task<bool> CancelQueuedTransaction()
    {
        using var cancellation = new CancellationTokenSource();
        var queued = Manager.RunTransactionAsync(
            _ =>
            {
                _events.Add("start:canceled");
                return Task.CompletedTask;
            },
            cancellation.Token);
        cancellation.Cancel();
        try
        {
            await queued;
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    public Task WaitUntilTransactionBlocked() => _blocked.Task;

    public Task ReleaseTransaction()
    {
        _gate.SetResult();
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

    public Task<string[]> GetEvents() => Task.FromResult(_events.ToArray());

    public Task<int> Get(string key)
    {
        _events.Add($"get:{key}");
        return Task.FromResult(dictionary.TryGetValue(key, out var value) ? value : -1);
    }

    public Task Deactivate()
    {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    private async Task BlockAsync(string key, int value)
    {
        _events.Add($"start:{key}");
        dictionary[key] = value;
        _blocked.TrySetResult();
        await _gate.Task;
        _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _events.Add($"end:{key}");
    }
}

[Reentrant]
public class ReentrantTransactionalGrain([FromKeyedServices("dictionary")] IDurableDictionary<string, int> dictionary)
    : TransactionalGrain(dictionary), IReentrantTransactionalGrain;
