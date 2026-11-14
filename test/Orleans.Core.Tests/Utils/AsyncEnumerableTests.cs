using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Orleans.Runtime.Utilities;
using TestExtensions;
using Xunit;

namespace UnitTests.RuntimeUtilities;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AsyncEnumerableDisposalCollection
{
    public const string Name = "Async update feed disposal";
}

[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
[Collection(AsyncEnumerableDisposalCollection.Name)]
public sealed class AsyncEnumerableTests
{
    [Fact]
    public async Task DisposePreservesBufferedValuesForSlowReaders()
    {
        var source = new AsyncEnumerable<int>(0, static (_, _) => true, static _ => { });
        await using var reader = source.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        source.Publish(1);
        source.Publish(2);
        source.Dispose();

        for (var expected = 0; expected <= 2; expected++)
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal(expected, reader.Current);
        }

        Assert.False(await reader.MoveNextAsync());
        Assert.False(await reader.MoveNextAsync());
    }

    [Fact]
    public void DisposeRefusesFunctionalUpdatesWithoutInvokingUserCallbacks()
    {
        var published = new List<int>();
        var updates = 0;
        var source = new AsyncEnumerable<int>(0, static (_, _) => true, published.Add);
        int Update(int current, int delta)
        {
            updates++;
            return current + delta;
        }

        Assert.True(source.TryPublish(Update, 1));
        source.Publish(Update, 1);
        source.Dispose();

        Assert.False(source.TryPublish(Update, 1));
        Assert.Throws<ObjectDisposedException>(() => source.Publish(Update, 1));
        Assert.Equal(2, updates);
        Assert.Equal([0, 1, 2], published);
    }

    [Fact]
    public async Task DisposeCompletesPendingAndFutureReadersWithoutPublishingAnotherValue()
    {
        var published = new List<int>();
        var source = new AsyncEnumerable<int>(0, static (_, _) => true, published.Add);
        var token = TestContext.Current.CancellationToken;
        await using var first = source.GetAsyncEnumerator(token);
        await using var second = source.GetAsyncEnumerator(token);
        Assert.True(await first.MoveNextAsync());
        Assert.True(await second.MoveNextAsync());
        Assert.Equal(0, first.Current);
        Assert.Equal(0, second.Current);
        var firstNext = first.MoveNextAsync().AsTask();
        var secondNext = second.MoveNextAsync().AsTask();
        Assert.False(firstNext.IsCompleted);
        Assert.False(secondNext.IsCompleted);

        source.Dispose();
        source.Dispose();
        Assert.False(await firstNext.WaitAsync(TimeSpan.FromSeconds(10), token));
        Assert.False(await secondNext.WaitAsync(TimeSpan.FromSeconds(10), token));
        Assert.False(await first.MoveNextAsync());
        Assert.False(await second.MoveNextAsync());
        await using var following = source.GetAsyncEnumerator(token);
        Assert.False(await following.MoveNextAsync());
        Assert.Throws<ObjectDisposedException>(() => first.Current);
        Assert.False(source.TryPublish(1));
        Assert.Throws<ObjectDisposedException>(() => source.Publish(1));
        Assert.Equal([0], published);
    }

    [Fact]
    public async Task ReaderCancellationLeavesOtherReadersAndPublicationHealthy()
    {
        var source = new AsyncEnumerable<int>(0, static (_, _) => true, static _ => { });
        using var cancellation = new CancellationTokenSource();
        var token = TestContext.Current.CancellationToken;
        await using var canceled = source.GetAsyncEnumerator(cancellation.Token);
        await using var healthy = source.GetAsyncEnumerator(token);
        Assert.True(await canceled.MoveNextAsync());
        Assert.True(await healthy.MoveNextAsync());
        var canceledNext = canceled.MoveNextAsync().AsTask();
        var healthyNext = healthy.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledNext.WaitAsync(TimeSpan.FromSeconds(10), token));
        Assert.False(healthyNext.IsCompleted);
        source.Publish(1);
        Assert.True(await healthyNext.WaitAsync(TimeSpan.FromSeconds(10), token));
        Assert.Equal(1, healthy.Current);
        source.Dispose();
        Assert.False(await healthy.MoveNextAsync());
    }

    [Fact]
    public void DisposeWithoutReadersDoesNotCreateUnobservedTaskFailures()
    {
        var failures = new ConcurrentQueue<Exception>();
        void Observe(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            // Observe only this test's distinct value type, not faults from unrelated tasks.
            if (sender is Task task && ContainsType(task.GetType(), typeof(UnobservedValue)))
            {
                foreach (var failure in args.Exception.Flatten().InnerExceptions)
                {
                    failures.Enqueue(failure);
                }
                args.SetObserved();
            }
        }
        TaskScheduler.UnobservedTaskException += Observe;
        try
        {
            var released = CreateDisposedSource();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(released.IsAlive);
            Assert.Empty(failures);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Observe;
        }
    }

    private static bool ContainsType(Type candidate, Type expected) => candidate == expected
        || candidate.GenericTypeArguments.Any(argument => ContainsType(argument, expected));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedSource()
    {
        var source = new AsyncEnumerable<UnobservedValue>(new(), static (_, _) => true, static _ => { });
        source.Dispose();
        return new WeakReference(source);
    }

    private sealed class UnobservedValue;
}
