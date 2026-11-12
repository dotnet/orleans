using System.Reflection;
using System.Threading.Tasks.Sources;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionRequestInvocationTests
{
    public static IEnumerable<object[]> InvocationCases()
    {
        foreach (var shape in new[] { "ValueTask", "ValueTaskOfInt", "Task", "TaskOfInt" })
        {
            foreach (var outcome in new[] { "Success", "Failure", "Cancellation" })
            {
                yield return [shape, outcome, false];
                yield return [shape, outcome, true];
            }

            yield return [shape, "Throw", false];
        }
    }

    [Theory]
    [MemberData(nameof(InvocationCases))]
    public async Task BaseInvoke_PreservesOutcomeAndConsumesResultOnce(string shape, string outcome, bool pending)
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = new InvalidOperationException("invocation failure");
        var source = new SingleConsumptionSource();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var usesValueTask = shape is "ValueTask" or "ValueTaskOfInt";

        void Complete()
        {
            switch (outcome)
            {
                case "Success":
                    if (usesValueTask) source.SetResult(42);
                    else completion.SetResult(42);
                    break;
                case "Failure":
                    if (usesValueTask) source.SetException(exception);
                    else completion.SetException(exception);
                    break;
                case "Cancellation":
                    if (usesValueTask) source.SetException(new OperationCanceledException(cancellation.Token));
                    else completion.SetCanceled(cancellation.Token);
                    break;
            }
        }

        if (!pending)
        {
            Complete();
        }

        var synchronousFailure = outcome == "Throw";
        Func<ValueTask<Response>> invoke = shape switch
        {
            "ValueTask" => new ValueTaskInvoker(services, () => synchronousFailure ? throw exception : new ValueTask(source, source.Version)).InvokeBody,
            "ValueTaskOfInt" => new ValueTaskInvokerOfInt(services, () => synchronousFailure ? throw exception : new ValueTask<int>(source, source.Version)).InvokeBody,
            "Task" => new TaskInvoker(services, () => synchronousFailure ? throw exception : completion.Task).InvokeBody,
            "TaskOfInt" => new TaskInvokerOfInt(services, () => synchronousFailure ? throw exception : completion.Task).InvokeBody,
            _ => throw new InvalidOperationException($"Unexpected return shape '{shape}'."),
        };

        var invocation = invoke();
        Assert.Equal(!pending, invocation.IsCompletedSuccessfully);
        if (pending)
        {
            Complete();
        }

        using var response = await invocation.AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        switch (outcome)
        {
            case "Success":
                Assert.Null(response.Exception);
                if (shape is "ValueTaskOfInt" or "TaskOfInt")
                {
                    Assert.Equal(42, response.GetResult<int>());
                }
                else
                {
                    Assert.Same(Response.Completed, response);
                }

                break;
            case "Failure":
            case "Throw":
                Assert.Same(exception, response.Exception);
                Assert.Same(exception, Assert.Throws<InvalidOperationException>(() => response.GetResult<int>()));
                break;
            case "Cancellation":
                var canceled = Assert.IsAssignableFrom<OperationCanceledException>(response.Exception);
                Assert.Equal(cancellation.Token, canceled.CancellationToken);
                Assert.Same(canceled, Assert.ThrowsAny<OperationCanceledException>(() => response.GetResult<int>()));
                break;
        }

        Assert.Equal(usesValueTask && !synchronousFailure ? 1 : 0, source.GetResultCalls);
    }

    private sealed class SingleConsumptionSource : IValueTaskSource, IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _source = new() { RunContinuationsAsynchronously = true };
        public short Version => _source.Version;
        public int GetResultCalls { get; private set; }
        public void SetResult(int value) => _source.SetResult(value);
        public void SetException(Exception exception) => _source.SetException(exception);
        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _source.OnCompleted(continuation, state, token, flags);

        public int GetResult(short token)
        {
            Assert.Equal(0, GetResultCalls++);
            return _source.GetResult(token);
        }

        void IValueTaskSource.GetResult(short token) => GetResult(token);
    }

    private sealed class ValueTaskInvoker(IServiceProvider services, Func<ValueTask> invoke)
        : TransactionRequest(services.GetRequiredService<Serializer<OrleansTransactionAbortedException>>(), services)
    {
        public ValueTask<Response> InvokeBody() => BaseInvoke();
        protected override ValueTask InvokeInner() => invoke();
        public override object GetTarget() => throw new NotSupportedException();
        public override void SetTarget(ITargetHolder holder) => throw new NotSupportedException();
        public override string GetMethodName() => throw new NotSupportedException();
        public override string GetInterfaceName() => throw new NotSupportedException();
        public override string GetActivityName() => throw new NotSupportedException();
        public override Type GetInterfaceType() => throw new NotSupportedException();
        public override MethodInfo GetMethod() => throw new NotSupportedException();
    }

    private sealed class ValueTaskInvokerOfInt(IServiceProvider services, Func<ValueTask<int>> invoke)
        : TransactionRequest<int>(services.GetRequiredService<Serializer<OrleansTransactionAbortedException>>(), services)
    {
        public ValueTask<Response> InvokeBody() => BaseInvoke();
        protected override ValueTask<int> InvokeInner() => invoke();
        public override object GetTarget() => throw new NotSupportedException();
        public override void SetTarget(ITargetHolder holder) => throw new NotSupportedException();
        public override string GetMethodName() => throw new NotSupportedException();
        public override string GetInterfaceName() => throw new NotSupportedException();
        public override string GetActivityName() => throw new NotSupportedException();
        public override Type GetInterfaceType() => throw new NotSupportedException();
        public override MethodInfo GetMethod() => throw new NotSupportedException();
    }

    private sealed class TaskInvoker(IServiceProvider services, Func<Task> invoke)
        : TransactionTaskRequest(services.GetRequiredService<Serializer<OrleansTransactionAbortedException>>(), services)
    {
        public ValueTask<Response> InvokeBody() => BaseInvoke();
        protected override Task InvokeInner() => invoke();
        public override object GetTarget() => throw new NotSupportedException();
        public override void SetTarget(ITargetHolder holder) => throw new NotSupportedException();
        public override string GetMethodName() => throw new NotSupportedException();
        public override string GetInterfaceName() => throw new NotSupportedException();
        public override string GetActivityName() => throw new NotSupportedException();
        public override Type GetInterfaceType() => throw new NotSupportedException();
        public override MethodInfo GetMethod() => throw new NotSupportedException();
    }

    private sealed class TaskInvokerOfInt(IServiceProvider services, Func<Task<int>> invoke)
        : TransactionTaskRequest<int>(services.GetRequiredService<Serializer<OrleansTransactionAbortedException>>(), services)
    {
        public ValueTask<Response> InvokeBody() => BaseInvoke();
        protected override Task<int> InvokeInner() => invoke();
        public override object GetTarget() => throw new NotSupportedException();
        public override void SetTarget(ITargetHolder holder) => throw new NotSupportedException();
        public override string GetMethodName() => throw new NotSupportedException();
        public override string GetInterfaceName() => throw new NotSupportedException();
        public override string GetActivityName() => throw new NotSupportedException();
        public override Type GetInterfaceType() => throw new NotSupportedException();
        public override MethodInfo GetMethod() => throw new NotSupportedException();
    }
}
