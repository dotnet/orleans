using System;
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
    public void ConcurrentRentResetReturnKeepsThreadLocalInstancesIsolated()
    {
        using var pool = new InvokablePool<TestInvokable>();
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        TestInvokable? first = null;
        TestInvokable? second = null;
        Exception? firstError = null;
        Exception? secondError = null;

        var firstThread = new Thread(() => Run(ref first, ref firstError));
        var secondThread = new Thread(() => Run(ref second, ref secondError));
        firstThread.Start();
        secondThread.Start();
        ready.Wait();
        start.Set();
        firstThread.Join();
        secondThread.Join();

        Assert.Null(firstError);
        Assert.Null(secondError);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);

        void Run(ref TestInvokable? firstItem, ref Exception? error)
        {
            try
            {
                ready.Signal();
                start.Wait();
                for (var i = 0; i < 10_000; i++)
                {
                    var item = pool.TryGet(out var pooled) ? pooled : new TestInvokable(pool);
                    firstItem ??= item;
                    Assert.Same(firstItem, item);
                    Assert.Equal(0, item.Number);
                    Assert.Null(item.Text);

                    item.Number = i + 1;
                    item.Text = i.ToString();
                    item.Dispose();
                }
            }
            catch (Exception exception)
            {
                error = exception;
            }
        }
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
