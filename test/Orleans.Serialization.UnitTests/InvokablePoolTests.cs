using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Orleans.Serialization.UnitTests
{
    [Trait("Category", "BVT")]
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

        private sealed class TestInvokable : IInvokable
        {
            public object GetTarget() => null;

            public void SetTarget(ITargetHolder holder) { }

            public ValueTask<Response> Invoke() => new(Response.Completed);

            public int GetArgumentCount() => 0;

            public object GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));

            public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));

            public string GetMethodName() => nameof(TestInvokable);

            public string GetInterfaceName() => nameof(TestInvokable);

            public string GetActivityName() => nameof(TestInvokable);

            public MethodInfo GetMethod() => null;

            public Type GetInterfaceType() => typeof(TestInvokable);

            public TimeSpan? GetDefaultResponseTimeout() => null;

            public CancellationToken GetCancellationToken() => CancellationToken.None;

            public bool TryCancel() => false;

            public bool IsCancellable => false;

            public void Dispose() { }
        }
    }
}
