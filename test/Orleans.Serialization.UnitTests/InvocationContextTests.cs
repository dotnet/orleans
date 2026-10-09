using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;

namespace Orleans.Serialization.UnitTests;

[Trait("Category", "BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
public sealed class InvocationContextTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddSerializer().BuildServiceProvider();
    private readonly InvocationContext _context;

    public InvocationContextTests()
    {
        _context = new InvocationContext(
            _services.GetRequiredService<CodecProvider>(),
            _services.GetRequiredService<CopyContextPool>(),
            _services.GetRequiredService<DeepCopier>().GetCopier<Response>());
    }

    public void Dispose() => _services.Dispose();

    [Theory]
    [InlineData("codecProvider")]
    [InlineData("copyContextPool")]
    [InlineData("responseCopier")]
    public void ConstructorRejectsNullServices(string parameterName)
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new InvocationContext(
            parameterName == "codecProvider" ? null! : _context.CodecProvider,
            parameterName == "copyContextPool" ? null! : _context.CopyContextPool,
            parameterName == "responseCopier" ? null! : _context.ResponseCopier));

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public async Task ConstructorRetainsProvidedServicesAfterInvocation()
    {
        var provider = _services.GetRequiredService<CodecProvider>();
        var contexts = _services.GetRequiredService<CopyContextPool>();
        var copier = _services.GetRequiredService<DeepCopier>().GetCopier<Response>();
        var context = new InvocationContext(provider, contexts, copier);
        var target = new InvocationTarget();

        Assert.Same(provider, context.CodecProvider);
        Assert.Same(contexts, context.CopyContextPool);
        Assert.Same(copier, context.ResponseCopier);
        foreach (var useValueTask in new[] { false, true })
        {
            using var request = CreateInvokable(useValueTask, target);
            using var response = await request.Invoke(context);
            Assert.Equal(InvocationTarget.CompletedResult, response.GetResult<int>());
        }

        Assert.Same(provider, context.CodecProvider);
        Assert.Same(contexts, context.CopyContextPool);
        Assert.Same(copier, context.ResponseCopier);
        Assert.Equal(1, target.TaskCalls);
        Assert.Equal(1, target.ValueTaskCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedContextualInvokeReturnsCompletedValueSynchronously(bool useValueTask)
    {
        var target = new InvocationTarget();
        using var request = CreateInvokable(useValueTask, target);

        var invocation = request.Invoke(_context);

        Assert.True(invocation.IsCompletedSuccessfully);
        using var response = await invocation;
        AssertGeneratedResult(response, InvocationTarget.CompletedResult);
        AssertCalls(target, useValueTask, 1);
        Assert.Same(target, request.GetTarget());
    }

    [Fact]
    public async Task GeneratedTaskInvokeWaitsForIncompleteCompletion()
    {
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new InvocationTarget { TaskCompletion = source.Task };
        using var request = CreateInvokable(false, target);

        var invocation = request.Invoke(_context);

        Assert.False(source.Task.IsCompleted);
        Assert.False(invocation.IsCompleted);
        AssertCalls(target, false, 1);
        source.SetResult(-101);
        using var response = await invocation;
        AssertGeneratedResult(response, -101);
        AssertCalls(target, false, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedValueTaskInvokeConsumesSourceExactlyOnce(bool completeBeforeInvoke)
    {
        var source = new SingleConsumptionSource();
        var target = new InvocationTarget { ValueTaskCompletion = source.CreateValueTask() };
        using var request = CreateInvokable(true, target);
        if (completeBeforeInvoke) source.SetResult(-211);

        var invocation = request.Invoke(_context);

        Assert.Equal(completeBeforeInvoke, invocation.IsCompletedSuccessfully);
        Assert.Equal(completeBeforeInvoke ? 1 : 0, source.GetResultCalls);
        Assert.Equal(completeBeforeInvoke ? 0 : 1, source.OnCompletedCalls);
        AssertCalls(target, true, 1);
        if (!completeBeforeInvoke)
        {
            Assert.False(invocation.IsCompleted);
            Assert.Equal(ValueTaskSourceStatus.Pending, source.Status);
            source.SetResult(-211);
        }

        using var response = await invocation;
        AssertGeneratedResult(response, -211);
        Assert.Equal(1, source.GetResultCalls);
        Assert.Equal(completeBeforeInvoke ? 0 : 1, source.OnCompletedCalls);
        AssertCalls(target, true, 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GeneratedTaskInvokePreservesFaultAndCancellationIdentity(bool completeBeforeInvoke, bool cancel)
    {
        var source = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception expected = cancel
            ? new OperationCanceledException("controlled Task cancellation", new CancellationToken(canceled: true))
            : new InvalidOperationException("controlled Task failure");
        if (completeBeforeInvoke) source.SetResult(0);
        // The async task builder retains the original OCE and marks the task canceled.
        // A bare TCS.SetCanceled() instead synthesizes a new exception on each GetResult.
        var target = new InvocationTarget { TaskCompletion = FailAfterBarrier(source.Task, expected) };
        using var request = CreateInvokable(false, target);

        var invocation = request.Invoke(_context);

        // The invocation succeeds with an exception envelope, not a faulted ValueTask.
        Assert.Equal(completeBeforeInvoke, invocation.IsCompletedSuccessfully);
        AssertCalls(target, false, 1);
        if (!completeBeforeInvoke)
        {
            Assert.False(invocation.IsCompleted);
            Assert.False(source.Task.IsCompleted);
            Assert.False(target.TaskCompletion.IsCompleted);
            source.SetResult(0);
        }

        using var response = await invocation;
        Assert.IsType<ExceptionResponse>(response);
        Assert.Same(expected, response.Exception);
        Assert.Same(expected, Assert.ThrowsAny<Exception>(() => response.GetResult<int>()));
        Assert.Equal(cancel, target.TaskCompletion.IsCanceled);
        Assert.Equal(!cancel, target.TaskCompletion.IsFaulted);
        AssertCalls(target, false, 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GeneratedValueTaskInvokePreservesFaultAndCancellationIdentity(bool completeBeforeInvoke, bool cancel)
    {
        var source = new SingleConsumptionSource();
        Exception expected = cancel
            ? new OperationCanceledException("controlled ValueTask cancellation", new CancellationToken(canceled: true))
            : new InvalidOperationException("controlled ValueTask failure");
        var target = new InvocationTarget { ValueTaskCompletion = source.CreateValueTask() };
        using var request = CreateInvokable(true, target);
        if (completeBeforeInvoke) source.SetException(expected);

        var invocation = request.Invoke(_context);

        Assert.Equal(completeBeforeInvoke, invocation.IsCompletedSuccessfully);
        Assert.Equal(completeBeforeInvoke ? 1 : 0, source.GetResultCalls);
        Assert.Equal(completeBeforeInvoke ? 0 : 1, source.OnCompletedCalls);
        AssertCalls(target, true, 1);
        if (!completeBeforeInvoke)
        {
            Assert.False(invocation.IsCompleted);
            Assert.Equal(ValueTaskSourceStatus.Pending, source.Status);
            source.SetException(expected);
        }

        using var response = await invocation;
        Assert.IsType<ExceptionResponse>(response);
        Assert.Same(expected, response.Exception);
        Assert.Same(expected, Assert.ThrowsAny<Exception>(() => response.GetResult<int>()));
        Assert.Equal(cancel ? ValueTaskSourceStatus.Canceled : ValueTaskSourceStatus.Faulted, source.Status);
        Assert.Equal(1, source.GetResultCalls);
        Assert.Equal(completeBeforeInvoke ? 0 : 1, source.OnCompletedCalls);
        AssertCalls(target, true, 1);
    }

    [Theory]
    [InlineData(false, false, "Distinct")]
    [InlineData(false, false, "Same")]
    [InlineData(false, false, "Throw")]
    [InlineData(false, true, "Distinct")]
    [InlineData(false, true, "Same")]
    [InlineData(false, true, "Throw")]
    [InlineData(true, false, "Distinct")]
    [InlineData(true, false, "Same")]
    [InlineData(true, false, "Throw")]
    [InlineData(true, true, "Distinct")]
    [InlineData(true, true, "Same")]
    [InlineData(true, true, "Throw")]
    public async Task GeneratedCompatibilityInvocationPreservesCopyOwnership(bool useValueTask, bool completeBeforeInvoke, string behavior)
    {
        using var services = new ServiceCollection().AddSerializer(builder => builder.Configure(options =>
            options.AddSerializer<int>(static _ => new Int32Codec(), static _ => new CompatibilityIntCopier())))
            .BuildServiceProvider();
        var copier = new TrackingResponseCopier(behavior);
        var context = new InvocationContext(
            services.GetRequiredService<CodecProvider>(),
            services.GetRequiredService<CopyContextPool>(),
            new DeepCopier<Response>(copier, services.GetRequiredService<CopyContextPool>()));
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completeBeforeInvoke) completion.SetResult(InvocationTarget.CompletedResult);
        var target = new InvocationTarget
        {
            TaskCompletion = completion.Task,
            ValueTaskCompletion = new(completion.Task)
        };
        using var request = CreateInvokable(useValueTask, target);

        var invocation = request.Invoke(context);

        Assert.Equal(completeBeforeInvoke, invocation.IsCompletedSuccessfully);
        if (!completeBeforeInvoke)
        {
            Assert.Equal(0, copier.Calls);
            completion.SetResult(InvocationTarget.CompletedResult);
        }
        using var response = await invocation;
        Assert.Equal(1, copier.Calls);
        Assert.IsType<Response<int>>(copier.Input);
        AssertCalls(target, useValueTask, 1);
        if (behavior == "Throw")
        {
            Assert.IsType<ExceptionResponse>(response);
            Assert.Same(copier.Failure, response.Exception);
        }
        else
        {
            Assert.IsType<Response<int>>(response);
            Assert.Equal(InvocationTarget.CompletedResult, response.GetResult<int>());
            Assert.Null(response.Exception);
        }
        if (behavior == "Same") Assert.Same(copier.Input, response);
        else
        {
            Assert.NotSame(copier.Input, response);
            Assert.Equal(0, copier.Input!.GetResult<int>());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedContextualEntryHasNoAsyncStateMachine(bool useValueTask)
    {
        using var request = CreateInvokable(useValueTask, new InvocationTarget());
        var contract = typeof(IInvokable).GetMethod(nameof(IInvokable.Invoke), new[] { typeof(InvocationContext) })!;
        var map = request.GetType().GetInterfaceMap(typeof(IInvokable));
        var index = Array.IndexOf(map.InterfaceMethods, contract);

        Assert.NotEqual(-1, index);
        var entry = map.TargetMethods[index];
        Assert.Equal(request.GetType(), entry.DeclaringType);
        Assert.True(entry.IsPrivate);
        Assert.Null(entry.GetCustomAttribute<AsyncStateMachineAttribute>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmedSynchronousInvocationAndDisposeAllocateZeroBytes(bool useValueTask)
    {
        const int warmupCount = 8_192;
        const int measuredCount = 1_024;
        var target = new InvocationTarget();
        using var request = CreateInvokable(useValueTask, target);

        using (var copyContext = _context.CopyContextPool.GetContext())
        using (var legacy = Response.FromResult(InvocationTarget.CompletedResult))
        using (var copy = _context.ResponseCopier.Copy(legacy))
        {
            Assert.Equal(InvocationTarget.CompletedResult, copy.GetResult<int>());
        }

        Response? warmedResponse = null;
        for (var i = 0; i < warmupCount; i++)
        {
            using var response = GetCompletedResponse(request.Invoke(_context));
            warmedResponse = response;
        }

        var allSynchronous = true;
        var resultSum = 0;
        Response? lastResponse = null;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < measuredCount; i++)
        {
            var invocation = request.Invoke(_context);
            allSynchronous &= invocation.IsCompletedSuccessfully;
            using var response = GetCompletedResponse(invocation);
            resultSum += response.GetResult<int>();
            lastResponse = response;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
        Assert.True(allSynchronous);
        Assert.Equal(measuredCount * InvocationTarget.CompletedResult, resultSum);
        AssertCalls(target, useValueTask, warmupCount + measuredCount);
        Assert.IsAssignableFrom<IRawResponseWriter>(lastResponse);
        Assert.Same(warmedResponse, lastResponse);
        Assert.Equal(0, lastResponse!.GetResult<int>());
    }

    [Fact]
    public void LegacyDisposeReturnsEnvelopeToGeneratedPool()
    {
        var original = ResponsePool.Get<int>();
        original.TypedResult = 73;

        original.Dispose();
        using var generated = ResponsePool.GetGenerated<Response<int>>();

        Assert.Same(original, generated);
        Assert.Equal(0, generated.TypedResult);
        Assert.Null(generated.Exception);
        Assert.Equal(typeof(int), generated.GetSimpleResultType());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedReturnSharesLegacyEnvelopePool(bool dispose)
    {
        var generated = ResponsePool.GetGenerated<Response<int>>();
        generated.TypedResult = -117;
        if (dispose)
        {
            generated.Dispose();
        }
        else
        {
            // Explicit ReturnGenerated requires the caller to clear its envelope first.
            generated.TypedResult = default;
            ResponsePool.ReturnGenerated(generated);
        }

        using var legacy = ResponsePool.Get<int>();
        Assert.Same(generated, legacy);
        Assert.Equal(0, legacy.TypedResult);
        Assert.Null(legacy.Exception);
        Assert.Equal(typeof(int), legacy.GetSimpleResultType());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedParameterlessInvokeRetainsLegacyResponse(bool useValueTask)
    {
        var target = new InvocationTarget();
        using var request = CreateInvokable(useValueTask, target);

        var invocation = request.Invoke();

        Assert.True(invocation.IsCompletedSuccessfully);
        using var response = await invocation;
        Assert.IsType<Response<int>>(response);
        Assert.Equal(InvocationTarget.CompletedResult, response.GetResult<int>());
        Assert.Null(response.Exception);
        AssertCalls(target, useValueTask, 1);
    }

    private static void AssertGeneratedResult(Response response, int expected)
    {
        Assert.IsAssignableFrom<IRawResponseWriter>(response);
        Assert.Equal(typeof(int), response.GetSimpleResultType());
        Assert.Equal(expected, response.GetResult<int>());
        Assert.Null(response.Exception);
    }

    private static void AssertCalls(InvocationTarget target, bool useValueTask, int expected)
    {
        Assert.Equal(useValueTask ? 0 : expected, target.TaskCalls);
        Assert.Equal(useValueTask ? expected : 0, target.ValueTaskCalls);
    }

    private static async Task<int> FailAfterBarrier(Task<int> barrier, Exception exception)
    {
        await barrier;
        throw exception;
    }

    private static Response GetCompletedResponse(ValueTask<Response> invocation)
    {
        // Allocation sampling must stay on this thread. Never block on an incomplete
        // operation: that is a failure of the synchronous path being measured.
        if (!invocation.IsCompletedSuccessfully)
            throw new InvalidOperationException("Expected a synchronously completed invocation.");
        return invocation.GetAwaiter().GetResult();
    }

    private static IInvokable CreateInvokable(bool useValueTask, InvocationTarget target)
    {
        var methodName = useValueTask ? nameof(IInvocationContextTestGrain.ValueTaskResult) : nameof(IInvocationContextTestGrain.TaskResult);
        var types = typeof(IInvocationContextTestGrain).Assembly.GetTypes()
            .Where(static type => !type.IsAbstract && typeof(IInvokable).IsAssignableFrom(type)
                && type.Name.StartsWith("Invokable_IInvocationContextTestGrain_", StringComparison.Ordinal));
        foreach (var type in types)
        {
            var request = (IInvokable)Activator.CreateInstance(type)!;
            if (request.GetMethodName() != methodName)
            {
                request.Dispose();
                continue;
            }

            Assert.Equal(typeof(IInvocationContextTestGrain), request.GetInterfaceType());
            if (useValueTask) Assert.IsAssignableFrom<Request<int>>(request);
            else Assert.IsAssignableFrom<TaskRequest<int>>(request);
            request.SetTarget(target);
            return request;
        }

        throw new InvalidOperationException($"No generated request was found for {methodName}.");
    }

    private sealed class InvocationTarget : IInvocationContextTestGrain, ITargetHolder
    {
        public const int CompletedResult = 913;
        public Task<int> TaskCompletion { get; init; } = Task.FromResult(CompletedResult);
        public ValueTask<int> ValueTaskCompletion { get; init; } = new(CompletedResult);
        public int TaskCalls { get; private set; }
        public int ValueTaskCalls { get; private set; }

        public Task<int> TaskResult()
        {
            TaskCalls++;
            return TaskCompletion;
        }

        public ValueTask<int> ValueTaskResult()
        {
            ValueTaskCalls++;
            return ValueTaskCompletion;
        }

        public object GetTarget() => this;
        public object? GetComponent(Type componentType) => componentType.IsInstanceOfType(this) ? this : null;
    }

    private sealed class SingleConsumptionSource : IValueTaskSource<int>
    {
        private ManualResetValueTaskSourceCore<int> _core = new() { RunContinuationsAsynchronously = true };
        private int _getResultCalls;
        private int _onCompletedCalls;

        public int GetResultCalls => Volatile.Read(ref _getResultCalls);
        public int OnCompletedCalls => Volatile.Read(ref _onCompletedCalls);
        public ValueTaskSourceStatus Status => _core.GetStatus(_core.Version);
        public ValueTask<int> CreateValueTask() => new(this, _core.Version);
        public void SetResult(int result) => _core.SetResult(result);
        public void SetException(Exception exception) => _core.SetException(exception);
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public int GetResult(short token)
        {
            if (Interlocked.Increment(ref _getResultCalls) != 1)
                throw new InvalidOperationException("ValueTask source was consumed more than once.");
            return _core.GetResult(token);
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            Interlocked.Increment(ref _onCompletedCalls);
            _core.OnCompleted(continuation, state, token, flags);
        }
    }

    private sealed class CompatibilityIntCopier : IDeepCopier<int>
    {
        public int DeepCopy(int input, CopyContext context) => input;
    }

    private sealed class TrackingResponseCopier(string behavior) : IDeepCopier<Response>
    {
        public int Calls { get; private set; }
        public Response? Input { get; private set; }
        public Exception Failure { get; } = new InvalidOperationException("response copying failed");

        [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(input))]
        public Response? DeepCopy(Response? input, CopyContext context)
        {
            if (input is null) return null;
            Calls++;
            Input = input;
            if (behavior == "Throw") throw Failure;
            return behavior == "Same" ? input : Response.FromResult(input.GetResult<int>());
        }
    }
}

public interface IInvocationContextTestGrain : IGrainWithIntegerKey
{
    [Id(0)]
    Task<int> TaskResult();

    [Id(1)]
    ValueTask<int> ValueTaskResult();
}
