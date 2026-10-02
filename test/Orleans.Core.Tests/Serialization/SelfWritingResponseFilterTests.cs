using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Serializers;
using Xunit;

namespace UnitTests.Serialization;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Serialization")]
[TestCategory("BVT"), TestCategory("Serialization")]
public sealed class SelfWritingResponseFilterTests
{
    [Fact]
    public async Task Invoke_DirectResponse_IsCopiedBeforeIncomingFilterResumesWithoutLegacyCopy()
    {
        using var services = CreateServices();
        var payload = new List<int> { 17, 23, 41 };
        var request = new DirectInvocation(payload);
        var legacyCopier = new CountingResponseCopier();
        var codecProvider = services.GetRequiredService<ICodecProvider>();
        var copyContexts = services.GetRequiredService<CopyContextPool>();
        var responseCopier = new DeepCopier<Response>(legacyCopier, copyContexts);
        var filterCalls = 0;
        var filter = new CallbackFilter(async context =>
        {
            filterCalls++;
            await context.Invoke();

            var result = Assert.IsType<List<int>>(context.Result);
            Assert.NotSame(payload, result);
            Assert.Equal(new[] { 17, 23, 41 }, result);
            Assert.Equal(1, request.CopyCalls);
            Assert.Equal(0, legacyCopier.CopyCalls);

            // Mutating grain-owned state here proves isolation precedes filter continuation.
            payload[0] = 99;
            payload.Add(73);
            Assert.Equal(new[] { 17, 23, 41 }, result);
            Assert.Same(result, context.Result);
        });
        var invoker = CreateInvoker(request, payload, filter, responseCopier, codecProvider, copyContexts);

        await invoker.Invoke();

        Assert.Equal(1, filterCalls);
        Assert.Equal(1, request.DirectCalls);
        Assert.Equal(1, request.CopyCalls);
        Assert.Equal(0, request.InvokeCalls);
        Assert.Equal(0, legacyCopier.CopyCalls);
        Assert.Same(codecProvider, request.CodecProvider);
        Assert.Same(copyContexts, request.CopyContexts);
        Assert.Same(responseCopier, request.ResponseCopier);
        Assert.Same(request.ReturnedResponse, invoker.Response);
        Assert.Equal(new[] { 17, 23, 41 }, Assert.IsType<List<int>>(invoker.Result));
        Assert.Equal(new[] { 99, 23, 41, 73 }, payload);
    }

    [Fact]
    public async Task Invoke_LegacyResponse_IsTransformedExactlyOnceBeforeIncomingFilterResumes()
    {
        using var services = CreateServices();
        var payload = new List<int> { 17, 23, 41 };
        using var originalResponse = new PayloadResponse(payload);
        var request = new LegacyInvocation(originalResponse, payload);
        var legacyCopier = new CountingResponseCopier(input =>
        {
            Assert.Same(originalResponse, input);
            return new PayloadResponse(Assert.IsType<List<int>>(input.Result).Select(value => value + 100).ToList());
        });
        var copyContexts = services.GetRequiredService<CopyContextPool>();
        var filterCalls = 0;
        var filter = new CallbackFilter(async context =>
        {
            filterCalls++;
            await context.Invoke();

            Assert.Equal(1, request.InvokeCalls);
            Assert.Equal(1, legacyCopier.CopyCalls);
            var result = Assert.IsType<List<int>>(context.Result);
            Assert.NotSame(payload, result);
            Assert.Equal(new[] { 117, 123, 141 }, result);
            payload[1] = -23;
            payload.Clear();
            Assert.Equal(new[] { 117, 123, 141 }, result);
        });
        var invoker = CreateInvoker(
            request, payload, filter, new DeepCopier<Response>(legacyCopier, copyContexts),
            services.GetRequiredService<ICodecProvider>(), copyContexts);

        await invoker.Invoke();

        Assert.Equal(1, filterCalls);
        Assert.Equal(1, request.InvokeCalls);
        Assert.Equal(1, legacyCopier.CopyCalls);
        Assert.Same(originalResponse, legacyCopier.LastInput);
        Assert.NotSame(originalResponse, invoker.Response);
        Assert.Equal(new[] { 117, 123, 141 }, Assert.IsType<List<int>>(invoker.Result));
        Assert.Empty(payload);
    }

    [Fact]
    public async Task Invoke_DirectExceptionResponse_PropagatesOriginalExceptionThroughIncomingFilter()
    {
        using var services = CreateServices();
        var payload = new List<int> { 17, 23, 41 };
        var failure = new InvalidOperationException("grain invocation failed");
        var request = new DirectInvocation(payload, failure);
        var legacyCopier = new CountingResponseCopier();
        var copyContexts = services.GetRequiredService<CopyContextPool>();
        var filterCalls = 0;
        var filter = new CallbackFilter(async context =>
        {
            filterCalls++;
            var caught = await Assert.ThrowsAsync<InvalidOperationException>(context.Invoke);
            Assert.Same(failure, caught);
            Assert.Same(failure, Assert.IsType<ExceptionResponse>(context.Response).Exception);
            Assert.Null(context.Result);
            throw caught;
        });
        var invoker = CreateInvoker(
            request, payload, filter, new DeepCopier<Response>(legacyCopier, copyContexts),
            services.GetRequiredService<ICodecProvider>(), copyContexts);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(invoker.Invoke);

        Assert.Same(failure, thrown);
        Assert.Equal("grain invocation failed", thrown.Message);
        Assert.Same(request.ReturnedResponse, invoker.Response);
        Assert.Same(failure, Assert.IsType<ExceptionResponse>(invoker.Response).Exception);
        Assert.Null(invoker.Result);
        Assert.Equal(1, filterCalls);
        Assert.Equal(1, request.DirectCalls);
        Assert.Equal(0, request.CopyCalls);
        Assert.Equal(0, request.InvokeCalls);
        Assert.Equal(0, legacyCopier.CopyCalls);
        Assert.Equal(new[] { 17, 23, 41 }, payload);
    }

    [Fact]
    public async Task Invoke_FilterSuppliesResponse_DoesNotInvokeOrCopyRequest()
    {
        using var services = CreateServices();
        var payload = new List<int> { 17, 23, 41 };
        var request = new DirectInvocation(payload);
        using var suppliedResponse = new PayloadResponse(new List<int> { 5, 8, 13 });
        var legacyCopier = new CountingResponseCopier();
        var copyContexts = services.GetRequiredService<CopyContextPool>();
        var filterCalls = 0;
        var filter = new CallbackFilter(context =>
        {
            filterCalls++;
            context.Response = suppliedResponse;
            return Task.CompletedTask;
        });
        var invoker = CreateInvoker(
            request, payload, filter, new DeepCopier<Response>(legacyCopier, copyContexts),
            services.GetRequiredService<ICodecProvider>(), copyContexts);

        await invoker.Invoke();

        Assert.Same(suppliedResponse, invoker.Response);
        Assert.Equal(new[] { 5, 8, 13 }, Assert.IsType<List<int>>(invoker.Result));
        Assert.Equal(1, filterCalls);
        Assert.Equal(0, request.DirectCalls);
        Assert.Equal(0, request.CopyCalls);
        Assert.Equal(0, request.InvokeCalls);
        Assert.Equal(0, legacyCopier.CopyCalls);
        Assert.Null(request.ReturnedResponse);
        Assert.Equal(new[] { 17, 23, 41 }, payload);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        return services.BuildServiceProvider();
    }

    private static GrainMethodInvoker CreateInvoker(
        IInvokable request,
        object grain,
        IIncomingGrainCallFilter filter,
        DeepCopier<Response> responseCopier,
        ICodecProvider codecProvider,
        CopyContextPool copyContexts)
    {
        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainInstance.Returns(grain);
        grainContext.GrainId.Returns(GrainId.Create("filter-test", "target"));
        var message = new Message
        {
            Direction = Message.Directions.Request,
            BodyObject = request,
            SendingGrain = GrainId.Create("filter-test", "sender"),
            TargetGrain = grainContext.GrainId
        };
        return new GrainMethodInvoker(
            message, grainContext, request, [filter], new InterfaceToImplementationMappingCache(),
            responseCopier, codecProvider, copyContexts);
    }

    private sealed class CallbackFilter(Func<IIncomingGrainCallContext, Task> callback) : IIncomingGrainCallFilter
    {
        public Task Invoke(IIncomingGrainCallContext context) => callback(context);
    }

    private class LegacyInvocation(Response response, object target) : IInvokable
    {
        public int InvokeCalls { get; protected set; }

        public virtual ValueTask<Response> Invoke()
        {
            InvokeCalls++;
            return ValueTask.FromResult(response);
        }

        public object GetTarget() => target;
        public void SetTarget(ITargetHolder holder) => throw new NotSupportedException();
        public int GetArgumentCount() => 0;
        public object? GetArgument(int index) => throw new ArgumentOutOfRangeException(nameof(index));
        public void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(nameof(index));
        public string GetMethodName() => nameof(ToString);
        public string GetInterfaceName() => nameof(IInvokable);
        public string GetActivityName() => $"{GetInterfaceName()}.{GetMethodName()}";
        public MethodInfo GetMethod() => typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes)!;
        public Type GetInterfaceType() => typeof(IInvokable);
        public void Dispose() { }
    }

    private sealed class DirectInvocation(List<int> payload, Exception? failure = null)
        : LegacyInvocation(new PayloadResponse(payload), payload), IResponseInvokable
    {
        public int DirectCalls { get; private set; }
        public int CopyCalls { get; private set; }
        public ICodecProvider? CodecProvider { get; private set; }
        public CopyContextPool? CopyContexts { get; private set; }
        public DeepCopier<Response>? ResponseCopier { get; private set; }
        public Response? ReturnedResponse { get; private set; }

        public override ValueTask<Response> Invoke()
        {
            InvokeCalls++;
            throw new InvalidOperationException("Direct invocations must not use the legacy Invoke path.");
        }

        public ValueTask<Response> InvokeAndCopy(
            ICodecProvider codecProvider, CopyContextPool copyContextPool, DeepCopier<Response> responseCopier)
        {
            DirectCalls++;
            CodecProvider = codecProvider;
            CopyContexts = copyContextPool;
            ResponseCopier = responseCopier;
            if (failure is not null)
            {
                ReturnedResponse = Response.FromException(failure);
            }
            else
            {
                CopyCalls++;
                ReturnedResponse = new PayloadResponse(new List<int>(payload));
            }

            return ValueTask.FromResult(ReturnedResponse);
        }
    }

    // These fakes intentionally have no serialization or grain-interface generation attributes.
    private sealed class PayloadResponse(List<int> payload) : Response
    {
        public override object? Result { get; set; } = payload;
        public override Exception? Exception { get => null; set => throw new NotSupportedException(); }
        public override T GetResult<T>() => (T)Result!;
        public override void Dispose() { }
    }

    private sealed class CountingResponseCopier(Func<Response, Response>? transform = null) : IDeepCopier<Response>
    {
        public int CopyCalls { get; private set; }
        public Response? LastInput { get; private set; }

        [return: NotNullIfNotNull(nameof(input))]
        public Response? DeepCopy(Response? input, CopyContext context)
        {
            CopyCalls++;
            LastInput = input;
            if (transform is null)
            {
                throw new InvalidOperationException("The direct response path must not invoke the legacy response copier.");
            }

            return input is null ? null : transform(input);
        }
    }
}
