using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Orleans.Serialization.UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
public class InvokablePoolTests
{
    [Fact]
    public void InvokablePoolsDoNotShareItemsAcrossInstances()
    {
        using var firstPool = new InvokablePool<TestInvokable>();
        using var secondPool = new InvokablePool<TestInvokable>();
        var item = new TestInvokable();

        firstPool.Return(item);

        Assert.False(secondPool.TryGet(out _));
        Assert.True(firstPool.TryGet(out var pooledItem));
        Assert.Same(item, pooledItem);
    }

    [Fact]
    public void ReturnAfterDisposeDoesNotThrow()
    {
        var pool = new InvokablePool<TestInvokable>();
        pool.Dispose();

        pool.Return(new TestInvokable());

        Assert.False(pool.TryGet(out _));
    }

    [Fact]
    public void RepeatedRentResetReturnReusesSingleInstance()
    {
        using var pool = new InvokablePool<TestInvokable>();
        TestInvokable? first = null;

        for (var i = 0; i < 10_000; i++)
        {
            var item = pool.TryGet(out var pooled) ? pooled : new TestInvokable(pool);
            first ??= item;

            Assert.Same(first, item);
            Assert.Equal(0, item.Number);
            Assert.Null(item.Text);
            Assert.Null(item.Payload);
            Assert.Null(item.Target);
            Assert.Equal(CancellationToken.None, item.Token);

            item.Number = i + 1;
            item.Text = i.ToString();
            item.Payload = new object();
            item.Target = new object();
            item.Token = new CancellationToken(canceled: true);
            item.Dispose();
        }
    }

    [Fact]
    public void ConcurrentRentResetReturnKeepsInstancesExclusive()
    {
        using var pool = new InvokablePool<TestInvokable>();
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        var active = new ConcurrentDictionary<TestInvokable, byte>();
        Exception? firstError = null;
        Exception? secondError = null;

        var firstThread = new Thread(() => Run(ref firstError));
        var secondThread = new Thread(() => Run(ref secondError));
        firstThread.Start();
        secondThread.Start();
        ready.Wait();
        start.Set();
        firstThread.Join();
        secondThread.Join();

        Assert.Null(firstError);
        Assert.Null(secondError);

        void Run(ref Exception? error)
        {
            try
            {
                ready.Signal();
                start.Wait();
                for (var i = 0; i < 10_000; i++)
                {
                    var item = pool.TryGet(out var pooled) ? pooled : new TestInvokable(pool);
                    Assert.True(active.TryAdd(item, 0));
                    Assert.Equal(0, item.Number);
                    Assert.Null(item.Text);

                    item.Number = i + 1;
                    item.Text = i.ToString();
                    Assert.True(active.TryRemove(item, out _));
                    item.Dispose();
                }
            }
            catch (Exception exception)
            {
                error = exception;
            }
        }
    }

    [Fact]
    public void CrossThreadReturnMakesItemAvailableToOtherThreads()
    {
        using var pool = new InvokablePool<TestInvokable>();
        var item = new TestInvokable(pool);
        Exception? error = null;
        using var returned = new ManualResetEventSlim();

        var returningThread = new Thread(() =>
        {
            try
            {
                item.Dispose();
                returned.Set();
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });

        returningThread.Start();
        returned.Wait();
        Assert.True(pool.TryGet(out var rented));
        returningThread.Join();

        Assert.Null(error);
        Assert.Same(item, rented);
    }

    private sealed class TestInvokable(InvokablePool<TestInvokable>? pool = null) : IInvokable
    {
        public int Number { get; set; }
        public string? Text { get; set; }
        public object? Payload { get; set; }
        public object? Target { get; set; }
        public CancellationToken Token { get; set; }

        public object GetTarget() => Target!;

        public void SetTarget(ITargetHolder holder) => Target = holder.GetTarget();

        public ValueTask<Response> Invoke() => new(Response.Completed);

        public int GetArgumentCount() => 0;

        public object GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));

        public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));

        public string GetMethodName() => nameof(TestInvokable);

        public string GetInterfaceName() => nameof(TestInvokable);

        public string GetActivityName() => nameof(TestInvokable);

        public MethodInfo GetMethod() => null!;

        public Type GetInterfaceType() => typeof(TestInvokable);

        public TimeSpan? GetDefaultResponseTimeout() => null;

        public CancellationToken GetCancellationToken() => Token;

        public bool TryCancel() => false;

        public bool IsCancellable => false;

        public void Dispose()
        {
            Number = 0;
            Text = null;
            Payload = null;
            Target = null;
            Token = default;
            pool?.Return(this);
        }
    }
}
