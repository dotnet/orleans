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
        CallbackDataOwner owner = default;
        owner = CallbackDataPool.Rent(
            CreateSharedData(_ =>
            {
                Interlocked.Increment(ref unregisterCount);
                CallbackDataPool.Return(owner);
            }),
            completion,
            new Message(),
            CreateInstruments(serviceProvider));
        using var lease = owner.Acquire();
        var callback = lease.Value;

        callback.SubscribeForCancellation(cancellation.Token);

        Assert.True(callback.IsCompleted);
        Assert.Equal(1, unregisterCount);
        var exception = Assert.IsType<OperationCanceledException>(completion.Response!.Exception);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CancellationSubscriptionAfterCompletionDoesNotRetainCompletionSource()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();

        var completionReference = CreateCompletedCallback(cancellation.Token, CreateInstruments(serviceProvider));

        for (var attempt = 0; attempt < 10 && completionReference.IsAlive; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(completionReference.IsAlive);
        GC.KeepAlive(cancellation);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void StaleOwnerCannotLeaseReusedCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        var instruments = CreateInstruments(serviceProvider);
        var staleOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        var lease = staleOwner.Acquire();
        var callback = lease.Value;
        lease.Dispose();
        CallbackDataPool.Return(staleOwner);

        var currentOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        using var currentLease = currentOwner.Acquire();
        var reusedCallback = currentLease.Value;
        Assert.Same(callback, reusedCallback);

        using var staleLease = staleOwner.Acquire();
        Assert.False(staleLease.TryGetValue(out _));

        CallbackDataPool.Return(currentOwner);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void ActiveLeaseDelaysCallbackReuse()
    {
        using var serviceProvider = CreateServiceProvider();
        var instruments = CreateInstruments(serviceProvider);
        var firstOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        var firstLease = firstOwner.Acquire();
        var firstCallback = firstLease.Value;
        CallbackDataPool.Return(firstOwner);

        var secondOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        using var secondLease = secondOwner.Acquire();
        Assert.NotSame(firstCallback, secondLease.Value);
        CallbackDataPool.Return(secondOwner);

        firstLease.Dispose();

        var reusedOwner = CallbackDataPool.Rent(CreateSharedData(), new TestResponseCompletionSource(), new Message(), instruments);
        using var reusedLease = reusedOwner.Acquire();
        Assert.Same(firstCallback, reusedLease.Value);
        CallbackDataPool.Return(reusedOwner);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void CancellationAfterCompletionDoesNotAffectReusedCallback()
    {
        using var serviceProvider = CreateServiceProvider();
        using var cancellation = new CancellationTokenSource();
        var instruments = CreateInstruments(serviceProvider);
        CallbackDataOwner completedOwner = default;
        completedOwner = CallbackDataPool.Rent(
            CreateSharedData(_ => CallbackDataPool.Return(completedOwner)),
            new TestResponseCompletionSource(),
            new Message(),
            instruments);
        var completedLease = completedOwner.Acquire();
        var completedCallback = completedLease.Value;
        completedCallback.SubscribeForCancellation(cancellation.Token);
        completedCallback.OnHostShutdown();
        completedLease.Dispose();

        var currentCompletion = new TestResponseCompletionSource();
        var currentOwner = CallbackDataPool.Rent(CreateSharedData(), currentCompletion, new Message(), instruments);
        using var currentLease = currentOwner.Acquire();
        Assert.Same(completedCallback, currentLease.Value);

        cancellation.Cancel();

        Assert.False(currentLease.Value.IsCompleted);
        Assert.Null(currentCompletion.Response);
        CallbackDataPool.Return(currentOwner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateCompletedCallback(CancellationToken cancellationToken, ApplicationRequestInstruments instruments)
    {
        var completion = new TestResponseCompletionSource();
        CallbackDataOwner owner = default;
        owner = CallbackDataPool.Rent(
            CreateSharedData(_ => CallbackDataPool.Return(owner)),
            completion,
            new Message(),
            instruments);
        using var lease = owner.Acquire();
        var callback = lease.Value;

        callback.OnHostShutdown();
        callback.SubscribeForCancellation(cancellationToken);

        return new WeakReference(completion);
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
        public Response? Response { get; private set; }

        public void Complete(Response value) => Response = value;

        public void Complete() => Response = Orleans.Serialization.Invocation.Response.Completed;
    }
}
