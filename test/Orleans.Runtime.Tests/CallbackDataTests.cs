using System;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace Tester;

public class CallbackDataTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void AlreadyCanceledTokenCompletesCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var completion = new TestResponseCompletionSource();
        var unregisterCount = 0;
        var callback = CreateCallback(
            completion,
            _ => Interlocked.Increment(ref unregisterCount),
            CreateInstruments(serviceProvider));

        callback.SubscribeForCancellation(cancellation.Token);

        Assert.True(callback.IsCompleted);
        Assert.Equal(1, unregisterCount);
        var exception = Assert.IsType<OperationCanceledException>(completion.Response.Exception);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CancellationSubscriptionAfterCompletionDoesNotRetainCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();

        var callbackReference = CreateCompletedCallback(cancellation.Token, CreateInstruments(serviceProvider));

        for (var attempt = 0; attempt < 10 && callbackReference.IsAlive; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(callbackReference.IsAlive);
        GC.KeepAlive(cancellation);
    }

    [Fact, TestCategory("BVT")]
    public void StaleOwnerCannotLeaseReusedCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        var instruments = CreateInstruments(serviceProvider);
        var callback = CallbackDataPool.Get();
        callback.Initialize(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        var staleOwner = new CallbackDataOwner(callback);
        CallbackDataPool.Return(staleOwner);

        var reusedCallback = CallbackDataPool.Get();
        Assert.Same(callback, reusedCallback);
        reusedCallback.Initialize(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        var currentOwner = new CallbackDataOwner(reusedCallback);

        using var staleLease = staleOwner.Acquire();
        Assert.False(staleLease.TryGetValue(out _));
        using var currentLease = currentOwner.Acquire();
        Assert.True(currentLease.TryGetValue(out var currentCallback));
        Assert.Same(reusedCallback, currentCallback);

        CallbackDataPool.Return(currentOwner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateCompletedCallback(CancellationToken cancellationToken, ApplicationRequestInstruments instruments)
    {
        var callback = CreateCallback(new TestResponseCompletionSource(), _ => { }, instruments);

        callback.OnHostShutdown();
        callback.SubscribeForCancellation(cancellationToken);

        return new WeakReference(callback);
    }

    private static CallbackData CreateCallback(
        IResponseCompletionSource completion,
        Action<Message> unregister,
        ApplicationRequestInstruments instruments)
    {
        var shared = CreateSharedData(unregister);
        return new CallbackData(shared, completion, new Message(), instruments);
    }

    private static SharedCallbackData CreateSharedData(Action<Message>? unregister = null) =>
        new(
            unregister ?? (_ => { }),
            logger: NullLogger<CallbackData>.Instance,
            responseTimeout: TimeSpan.FromMinutes(1),
            cancelOnTimeout: false,
            waitForCancellationAcknowledgement: false,
            cancellationManager: null);

    private static ServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        return services.BuildServiceProvider();
    }

    private static ApplicationRequestInstruments CreateInstruments(IServiceProvider serviceProvider) =>
        new(new OrleansInstruments(serviceProvider.GetRequiredService<IMeterFactory>()));

    private sealed class TestResponseCompletionSource : IResponseCompletionSource
    {
        public Response Response { get; private set; } = null!;

        public void Complete(Response value) => Response = value;

        public void Complete() => Response = Orleans.Serialization.Invocation.Response.Completed;
    }
}
