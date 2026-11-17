using System.Diagnostics.Metrics;
using System.Reflection;
using System.Threading.Tasks.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Invocation;
using Xunit;

namespace UnitTests.Runtime;

[TestCategory("BVT")]
public class InvocationResultCopyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task InvokeCopiesResultGraphOnlyWhenRequested(bool valueTask, bool pending, bool copy)
    {
        using var services = CreateServices();
        var original = CreatePayload();
        var completion = new TaskCompletionSource<InvocationPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending) completion.SetResult(original);
        using var request = CreateRequest(valueTask, () => completion.Task);

        var invocation = copy
            ? request.Invoke(services.GetRequiredService<CopyContextPool>())
            : request.Invoke();
        if (pending)
        {
            Assert.False(invocation.IsCompleted);
            completion.SetResult(original);
        }

        using var response = await invocation;
        var result = Assert.IsType<InvocationPayload>(response.GetResult<InvocationPayload>());
        Assert.Same(result, result.Next);
        Assert.Same(result.First, result.Second);
        Assert.Equal([7, 9], result.First);
        if (copy)
        {
            Assert.NotSame(original, result);
            Assert.NotSame(original.First, result.First);
            result.First.Add(11);
            Assert.Equal([7, 9], original.First);
        }
        else
        {
            Assert.Same(original, result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeCopiesNullResult(bool valueTask)
    {
        using var services = CreateServices();
        using var request = CreateRequest(valueTask, () => Task.FromResult<InvocationPayload>(null!));
        using var response = await request.Invoke(services.GetRequiredService<CopyContextPool>());
        Assert.Null(response.GetResult<InvocationPayload>());
        Assert.Null(response.Exception);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvocationFailurePreservesException(bool valueTask, bool pending)
    {
        using var services = CreateServices();
        var failure = new InvalidOperationException("invocation failed");
        var completion = new TaskCompletionSource<InvocationPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending) completion.SetException(failure);
        using var request = CreateRequest(valueTask, () => completion.Task);

        var invocation = request.Invoke(services.GetRequiredService<CopyContextPool>());
        if (pending) completion.SetException(failure);
        using var response = await invocation;
        Assert.Same(failure, response.Exception);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvocationCancellationPreservesToken(bool valueTask, bool pending)
    {
        using var services = CreateServices();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var completion = new TaskCompletionSource<InvocationPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending) completion.SetCanceled(cancellation.Token);
        using var request = CreateRequest(valueTask, () => completion.Task);

        var invocation = request.Invoke(services.GetRequiredService<CopyContextPool>());
        if (pending) completion.SetCanceled(cancellation.Token);
        using var response = await invocation;
        var exception = Assert.IsAssignableFrom<OperationCanceledException>(response.Exception);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValueTaskSourceIsConsumedOnce(bool pending)
    {
        using var services = CreateServices();
        var source = new SingleConsumptionSource();
        var original = CreatePayload();
        if (!pending) source.Complete(original);
        using var request = new ValueTaskInvoker<InvocationPayload>(() => source.ValueTask);

        var invocation = request.Invoke(services.GetRequiredService<CopyContextPool>());
        if (pending) source.Complete(original);
        using var response = await invocation;

        Assert.NotSame(original, response.GetResult<InvocationPayload>());
        Assert.Null(response.Exception);
        Assert.Equal(1, source.Consumptions);
    }

    [Fact]
    public async Task InvokeCopiesPolymorphicResult()
    {
        using var services = CreateServices();
        var original = CreatePayload();
        using var request = new TaskInvoker<object>(() => Task.FromResult<object>(original));

        using var response = await request.Invoke(services.GetRequiredService<CopyContextPool>());

        var result = Assert.IsType<InvocationPayload>(response.Result);
        Assert.NotSame(original, result);
        Assert.Same(result, result.Next);
        Assert.Equal([7, 9], result.First);
    }

    [Fact]
    public async Task ResultCopierOverridesAreScopedToProvider()
    {
        using var first = CreateServices(new ReplacingPayloadCopier(1));
        using var second = CreateServices(new ReplacingPayloadCopier(2));
        using var request = CreateRequest(valueTask: false, () => Task.FromResult(CreatePayload()));

        using var firstResponse = await request.Invoke(first.GetRequiredService<CopyContextPool>());
        using var secondResponse = await request.Invoke(second.GetRequiredService<CopyContextPool>());

        Assert.Equal([1], Assert.IsType<InvocationPayload>(firstResponse.Result).First);
        Assert.Equal([2], Assert.IsType<InvocationPayload>(secondResponse.Result).First);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObserverSendsIsolatedInvocationAndFilterResults(bool replaceResult)
    {
        var replacement = CreatePayload();
        replacement.First.Add(13);
        IIncomingGrainCallFilter? filter = replaceResult
            ? new ResultFilter(async context =>
            {
                await context.Invoke();
                context.Result = replacement;
            })
            : null;
        using var services = CreateServices(filter: filter);
        var runtime = Substitute.For<IRuntimeClient>();
        runtime.ServiceProvider.Returns(services);
        Response? received = null;
        runtime.When(client => client.SendResponse(Arg.Any<Message>(), Arg.Any<Response>()))
            .Do(call => received = call.Arg<Response>());
        var instruments = new OrleansInstruments(services.GetRequiredService<IMeterFactory>());
        var trace = new MessagingTrace(
            NullLoggerFactory.Instance, new MessagingInstruments(instruments), new MessagingProcessingInstruments(instruments));
        var manager = new InvokableObjectManager(
            Substitute.For<IGrainContext>(), runtime, services.GetRequiredService<DeepCopier>(), trace,
            services.GetRequiredService<CopyContextPool>(), new InterfaceToImplementationMappingCache(),
            NullLogger<InvokableObjectManager>.Instance);
        var observerId = ObserverGrainId.Create(ClientGrainId.Create("copy-result"), IdSpan.Create("observer"));
        Assert.True(manager.TryRegister(Substitute.For<IGrainObserver>(), observerId));
        var original = CreatePayload();
        using var request = CreateRequest(valueTask: false, () => Task.FromResult(original));
        using var message = new Message
        {
            Direction = Message.Directions.Request,
            TargetGrain = observerId.GrainId,
            SendingGrain = ClientGrainId.Create("copy-result-sender").GrainId,
            BodyObject = request
        };

        manager.Dispatch(message);
        await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        using var response = Assert.IsAssignableFrom<Response>(received);
        var result = Assert.IsType<InvocationPayload>(response.Result);
        Assert.NotSame(original, result);
        Assert.NotSame(replacement, result);
        Assert.Same(result, result.Next);
        Assert.Equal(replaceResult ? [7, 9, 13] : new[] { 7, 9 }, result.First);
        replacement.First.Add(17);
        original.First.Add(19);
        Assert.Equal(replaceResult ? [7, 9, 13] : new[] { 7, 9 }, result.First);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncomingFilterObservesCopiedResultExceptForLocalRequests(bool localOnly)
    {
        using var services = CreateServices();
        var original = CreatePayload();
        using var request = CreateRequest(valueTask: false, () => Task.FromResult(original));
        InvocationPayload? observed = null;
        var filter = new ResultFilter(async context =>
        {
            await context.Invoke();
            observed = Assert.IsType<InvocationPayload>(context.Result);
            observed.First.Add(11);
        });
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainInstance.Returns(new object());
        var invoker = new GrainMethodInvoker(
            new Message { IsLocalOnly = localOnly }, grainContext, request, [filter],
            new InterfaceToImplementationMappingCache(), services.GetRequiredService<CopyContextPool>());

        await invoker.Invoke();

        using var response = Assert.IsAssignableFrom<Response>(invoker.Response);
        Assert.NotNull(observed);
        Assert.Same(observed, response.GetResult<InvocationPayload>());
        Assert.Equal([7, 9, 11], observed.First);
        if (localOnly)
        {
            Assert.Same(original, observed);
        }
        else
        {
            Assert.NotSame(original, observed);
            Assert.Equal([7, 9], original.First);
        }
    }

    [Fact]
    public async Task IncomingFilterCanHandleResultCopyFailure()
    {
        var failure = new InvalidOperationException("copy failed");
        using var services = CreateServices(new FailingPayloadCopier(failure));
        using var request = CreateRequest(valueTask: false, () => Task.FromResult(CreatePayload()));
        var filter = new ResultFilter(async context =>
        {
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(context.Invoke));
            context.Result = "handled";
        });
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainInstance.Returns(new object());
        var invoker = new GrainMethodInvoker(
            new Message(), grainContext, request, [filter],
            new InterfaceToImplementationMappingCache(), services.GetRequiredService<CopyContextPool>());

        await invoker.Invoke();

        using var response = Assert.IsAssignableFrom<Response>(invoker.Response);
        Assert.Equal("handled", response.GetResult<string>());
    }

    private static ServiceProvider CreateServices(
        IDeepCopier<InvocationPayload>? copier = null,
        IIncomingGrainCallFilter? filter = null)
    {
        var services = new ServiceCollection()
            .AddMetrics()
            .AddSerializer(builder => builder.AddAssembly(typeof(InvocationResultCopyTests).Assembly))
            .Configure<TypeManifestOptions>(options =>
            {
                options.AddSerializer<Response<InvocationPayload>>(
                    _ => throw new NotSupportedException(),
                    _ => throw new InvalidOperationException("The response wrapper copier must not be resolved."));
                if (copier is not null)
                {
                    options.AddSerializer<InvocationPayload>(_ => throw new NotSupportedException(), _ => copier);
                }
            });
        if (filter is not null) services.AddSingleton(filter);
        return services.BuildServiceProvider();
    }

    private static InvocationPayload CreatePayload()
    {
        var values = new List<int> { 7, 9 };
        var result = new InvocationPayload { First = values, Second = values };
        result.Next = result;
        return result;
    }

    private static RequestBase CreateRequest(bool valueTask, Func<Task<InvocationPayload>> callback) =>
        valueTask
            ? new ValueTaskInvoker<InvocationPayload>(() => new(callback()))
            : new TaskInvoker<InvocationPayload>(callback);

    private sealed class ResultFilter(Func<IIncomingGrainCallContext, Task> callback) : IIncomingGrainCallFilter
    {
        public Task Invoke(IIncomingGrainCallContext context) => callback(context);
    }

    private sealed class FailingPayloadCopier(Exception failure) : IDeepCopier<InvocationPayload>
    {
        public InvocationPayload DeepCopy(InvocationPayload? input, CopyContext context) => throw failure;
    }

    private sealed class ReplacingPayloadCopier(int marker) : IDeepCopier<InvocationPayload>
    {
        public InvocationPayload DeepCopy(InvocationPayload? input, CopyContext context) => new() { First = [marker] };
    }

    private sealed class SingleConsumptionSource : IValueTaskSource<InvocationPayload>
    {
        private ManualResetValueTaskSourceCore<InvocationPayload> _source;
        public int Consumptions { get; private set; }
        public ValueTask<InvocationPayload> ValueTask => new(this, _source.Version);
        public void Complete(InvocationPayload value) => _source.SetResult(value);
        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _source.OnCompleted(continuation, state, token, flags);
        public InvocationPayload GetResult(short token)
        {
            Assert.Equal(0, Consumptions);
            Consumptions++;
            return _source.GetResult(token);
        }
    }

    private sealed class TaskInvoker<TResult>(Func<Task<TResult>> callback) : TaskRequest<TResult>
    {
        protected override Task<TResult> InvokeInner() => callback();
        public override object GetTarget() => this;
        public override void SetTarget(ITargetHolder holder) { }
        public override string GetMethodName() => nameof(InvokeInner);
        public override string GetInterfaceName() => nameof(TaskInvoker<TResult>);
        public override string GetActivityName() => nameof(TaskInvoker<TResult>);
        public override Type GetInterfaceType() => typeof(TaskInvoker<TResult>);
        public override MethodInfo GetMethod() => throw new NotSupportedException();
        public override void Dispose() { }
    }

    private sealed class ValueTaskInvoker<TResult>(Func<ValueTask<TResult>> callback) : Request<TResult>
    {
        protected override ValueTask<TResult> InvokeInner() => callback();
        public override object GetTarget() => this;
        public override void SetTarget(ITargetHolder holder) { }
        public override string GetMethodName() => nameof(InvokeInner);
        public override string GetInterfaceName() => nameof(ValueTaskInvoker<TResult>);
        public override string GetActivityName() => nameof(ValueTaskInvoker<TResult>);
        public override Type GetInterfaceType() => typeof(ValueTaskInvoker<TResult>);
        public override MethodInfo GetMethod() => throw new NotSupportedException();
        public override void Dispose() { }
    }
}

[GenerateSerializer]
public sealed class InvocationPayload
{
    [Id(0)] public InvocationPayload? Next { get; set; }
    [Id(1)] public List<int> First { get; set; } = [];
    [Id(2)] public List<int> Second { get; set; } = [];
}
