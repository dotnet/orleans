using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using TestExtensions;
using UnitTests.Messaging;
using Xunit;

namespace UnitTests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class GrainReferenceRuntimeTests
{
    [Fact, TestCategory("BVT")]
    public void TypedCompletionSourceReturnsToPoolWhenSendThrows()
    {
        var runtimeClient = new ThrowingRuntimeClient();
        var runtime = CreateRuntime(runtimeClient);
        var request = new TestInvokableRequest();

        Assert.Throws<InvalidOperationException>(() => runtime.InvokeMethodAsync<int>(null!, request, InvokeMethodOptions.None));

        var captured = Assert.IsType<ResponseCompletionSource<int>>(runtimeClient.Context);
        var returned = ResponseCompletionSourcePool.Get<int>();
        Assert.Same(captured, returned);
        Assert.Equal(1, request.DisposeCount);
        returned.Reset();
    }

    [Fact, TestCategory("BVT")]
    public void UntypedCompletionSourceReturnsToPoolWhenSendThrows()
    {
        var runtimeClient = new ThrowingRuntimeClient();
        var runtime = CreateRuntime(runtimeClient);
        var request = new TestInvokableRequest();

        Assert.Throws<InvalidOperationException>(() => runtime.InvokeMethodAsync(null!, request, InvokeMethodOptions.None));

        var captured = Assert.IsType<ResponseCompletionSource>(runtimeClient.Context);
        var returned = ResponseCompletionSourcePool.Get();
        Assert.Same(captured, returned);
        Assert.Equal(1, request.DisposeCount);
        returned.Reset();
    }

    [Fact, TestCategory("BVT")]
    public async Task RequestOwnershipTransfersWhenSendReturns()
    {
        var runtimeClient = new ThrowingRuntimeClient(throwOnSend: false);
        var runtime = CreateRuntime(runtimeClient);
        var request = new TestInvokableRequest();

        var resultTask = runtime.InvokeMethodAsync<int>(null!, request, InvokeMethodOptions.None);

        Assert.Equal(1, request.DisposeCount);
        using var response = Response.FromResult(42);
        runtimeClient.Context!.Complete(response);
        Assert.Equal(42, await resultTask);
    }

    private static GrainReferenceRuntime CreateRuntime(IRuntimeClient runtimeClient)
        => new(runtimeClient, null!, [], null!, null!, null!);

    private sealed class ThrowingRuntimeClient(bool throwOnSend = true) : IRuntimeClient
    {
        public IResponseCompletionSource? Context { get; private set; }

        public TimeProvider TimeProvider => TimeProvider.System;
        public IInternalGrainFactory InternalGrainFactory => throw new NotSupportedException();
        public string CurrentActivationIdentity => string.Empty;
        public IServiceProvider ServiceProvider => EmptyServiceProvider.Instance;
        public IGrainReferenceRuntime GrainReferenceRuntime => throw new NotSupportedException();

        public TimeSpan GetResponseTimeout() => throw new NotSupportedException();

        public void SetResponseTimeout(TimeSpan timeout) => throw new NotSupportedException();

        public void SendRequest(GrainReference target, IInvokable request, IResponseCompletionSource? context, InvokeMethodOptions options)
        {
            Context = context;
            request.Dispose();
            if (throwOnSend)
            {
                throw new InvalidOperationException("Send failed.");
            }
        }

        public void SendResponse(Message request, Response response) => throw new NotSupportedException();

        public void ReceiveResponse(Message message) => throw new NotSupportedException();

        public IAddressable CreateObjectReference(IAddressable obj) => throw new NotSupportedException();

        public void DeleteObjectReference(IAddressable obj) => throw new NotSupportedException();

        public void BreakOutstandingMessagesToSilo(SiloAddress deadSilo) => throw new NotSupportedException();

        public int GetRunningRequestsCount(GrainInterfaceType grainInterfaceType) => 0;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }

}
