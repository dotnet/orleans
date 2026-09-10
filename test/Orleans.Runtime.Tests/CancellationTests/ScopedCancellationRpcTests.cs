using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Tester;

[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
public class ScopedCancellationRpcTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GeneratedProxyKeepsOnlyOptedInCallsPending(bool insideSilo, bool withFilters)
    {
        await using var cluster = CreateCluster(withFilters);
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var (proxy, target, runtime) = GetTarget(cluster, insideSilo);
        var interfaceType = ((GrainReference)proxy).InterfaceType;

        foreach (var generic in new[] { true, false })
        {
            foreach (var optedIn in new[] { true, false, true, false })
            {
                var call = target.AddCall();
                using var cancellation = new CancellationTokenSource();
                try
                {
                    Task pending;
                    if (optedIn)
                    {
                        using var scope = CancellationAcknowledgementScope.Enter();
                        pending = Invoke(proxy, call.Id, generic, cancellation.Token);
                    }
                    else
                    {
                        pending = Invoke(proxy, call.Id, generic, cancellation.Token);
                    }

                    await Wait(call.Entered.Task);
                    Assert.Equal(1, runtime.GetRunningRequestsCount(interfaceType));
                    var localWait = pending.WaitAsync(cancellation.Token);
                    cancellation.Cancel();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(localWait));
                    await Wait(call.CancellationObserved.Task);

                    if (optedIn)
                    {
                        Assert.False(pending.IsCompleted);
                        Assert.Equal(1, runtime.GetRunningRequestsCount(interfaceType));
                        call.Release.SetResult();
                        await Wait(pending);
                        if (generic)
                        {
                            Assert.Equal(42, await (Task<int>)pending);
                        }
                    }
                    else
                    {
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(pending));
                    }

                    Assert.Equal(0, runtime.GetRunningRequestsCount(interfaceType));
                }
                finally
                {
                    call.Release.TrySetResult();
                    await Wait(call.Exited.Task);
                    target.RemoveCall(call.Id);
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncFilterForwardsCapturedPolicyWithoutOptingInNestedCalls(bool insideSilo)
    {
        var filter = new ForwardingFilter();
        await using var cluster = CreateCluster(withFilters: true, filter);
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var (proxy, target, runtime) = GetTarget(cluster, insideSilo);
        var original = target.AddCall();
        var nested = target.AddCall();
        var nestedStarted = new TaskCompletionSource<Task<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var forward = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var originalCancellation = new CancellationTokenSource();
        using var nestedCancellation = new CancellationTokenSource();
        filter.BeforeForward[original.Id] = () =>
        {
            nestedStarted.SetResult(proxy.RunAsync(nested.Id, nestedCancellation.Token));
            return forward.Task;
        };

        try
        {
            Task<int> pending;
            using (CancellationAcknowledgementScope.Enter())
            {
                pending = proxy.RunAsync(original.Id, originalCancellation.Token);
            }

            var nestedTask = await nestedStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            await Wait(nested.Entered.Task);
            nestedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(nestedTask));
            await Wait(nested.CancellationObserved.Task);
            nested.Release.SetResult();

            forward.SetResult();
            await Wait(original.Entered.Task);
            originalCancellation.Cancel();
            await Wait(original.CancellationObserved.Task);
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, runtime.GetRunningRequestsCount(((GrainReference)proxy).InterfaceType));
            original.Release.SetResult();
            Assert.Equal(42, await pending.WaitAsync(TestTimeout, TestContext.Current.CancellationToken));
        }
        finally
        {
            forward.TrySetResult();
            original.Release.TrySetResult();
            nested.Release.TrySetResult();
            await Wait(Task.WhenAll(original.Exited.Task, nested.Exited.Task));
            target.RemoveCall(original.Id);
            target.RemoveCall(nested.Id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedProxySynchronousThrowRestoresInvocationScope(bool insideSilo)
    {
        await using var cluster = CreateCluster(withFilters: false);
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var (proxy, target, _) = GetTarget(cluster, insideSilo);
        using var alreadyCanceled = new CancellationTokenSource();
        alreadyCanceled.Cancel();
        using (CancellationAcknowledgementScope.Enter())
        {
            Assert.Throws<OperationCanceledException>(() =>
            {
                using var nested = CancellationAcknowledgementScope.Enter();
                _ = proxy.RunAsync(Guid.NewGuid(), alreadyCanceled.Token);
            });
            using var captured = CancellationAcknowledgementScope.Capture();
            Assert.True(captured.WaitForAcknowledgement);
        }

        using var cancellation = new CancellationTokenSource();
        var call = target.AddCall();
        try
        {
            var ordinary = proxy.RunAsync(call.Id, cancellation.Token);
            await Wait(call.Entered.Task);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wait(ordinary));
            await Wait(call.CancellationObserved.Task);
        }
        finally
        {
            call.Release.TrySetResult();
            await Wait(call.Exited.Task);
            target.RemoveCall(call.Id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalHostShutdownTerminatesOptedInRpc(bool insideSilo)
    {
        await using var cluster = CreateCluster(withFilters: false);
        await cluster.DeployAsync(TestContext.Current.CancellationToken);
        var (proxy, target, _) = GetTarget(cluster, insideSilo);
        var call = target.AddCall();
        using var cancellation = new CancellationTokenSource();
        try
        {
            Task<int> pending;
            using (CancellationAcknowledgementScope.Enter())
            {
                pending = proxy.RunAsync(call.Id, cancellation.Token);
            }

            await Wait(call.Entered.Task);
            cancellation.Cancel();
            await Wait(call.CancellationObserved.Task);
            Assert.False(pending.IsCompleted);
            if (insideSilo)
            {
                await cluster.Silos[0].StopSiloAsync(stopGracefully: false);
            }
            else
            {
                await cluster.StopClusterClientAsync(TestContext.Current.CancellationToken);
            }

            await Assert.ThrowsAsync<SiloUnavailableException>(() => Wait(pending));
        }
        finally
        {
            call.Release.TrySetResult();
            await Wait(call.Exited.Task);
            target.RemoveCall(call.Id);
        }
    }

    private static Task Invoke(IScopedCancellationTestTarget proxy, Guid callId, bool generic, CancellationToken cancellationToken) =>
        generic ? proxy.RunAsync(callId, cancellationToken) : proxy.RunVoidAsync(callId, cancellationToken);

    private static Task Wait(Task task) => task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

    private static InProcessTestCluster CreateCluster(bool withFilters, ForwardingFilter? filter = null)
    {
        filter ??= new ForwardingFilter();
        var builder = new InProcessTestClusterBuilder(2);
        builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            siloBuilder.Configure<SiloMessagingOptions>(options => options.WaitForCancellationAcknowledgement = false);
            siloBuilder.Services.AddSingleton<ScopedCancellationTestTarget>();
            siloBuilder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(services => services.GetRequiredService<ScopedCancellationTestTarget>());
            if (withFilters)
            {
                siloBuilder.Services.AddSingleton<IOutgoingGrainCallFilter>(filter);
            }
        });
        builder.ConfigureClient(clientBuilder =>
        {
            clientBuilder.Configure<ClientMessagingOptions>(options => options.WaitForCancellationAcknowledgement = false);
            if (withFilters)
            {
                clientBuilder.Services.AddSingleton<IOutgoingGrainCallFilter>(filter);
            }
        });
        return builder.Build();
    }

    private static (IScopedCancellationTestTarget Proxy, ScopedCancellationTestTarget Target, IRuntimeClient Runtime) GetTarget(InProcessTestCluster cluster, bool insideSilo)
    {
        var services = insideSilo ? cluster.Silos[0].ServiceProvider : cluster.Client.ServiceProvider;
        var factory = services.GetRequiredService<IInternalGrainFactory>();
        var destination = cluster.Silos[1];
        var proxy = factory.GetSystemTarget<IScopedCancellationTestTarget>(ScopedCancellationTestTarget.TargetType, destination.SiloAddress);
        return (proxy, destination.ServiceProvider.GetRequiredService<ScopedCancellationTestTarget>(), services.GetRequiredService<IRuntimeClient>());
    }

    private sealed class ForwardingFilter : IOutgoingGrainCallFilter
    {
        public ConcurrentDictionary<Guid, Func<Task>> BeforeForward { get; } = new();

        public async Task Invoke(IOutgoingGrainCallContext context)
        {
            if (context.InterfaceMethod.DeclaringType == typeof(IScopedCancellationTestTarget))
            {
                await Task.Yield();
                if (BeforeForward.TryRemove((Guid)context.Request.GetArgument(0)!, out var beforeForward))
                {
                    await beforeForward();
                }
            }

            await context.Invoke();
        }
    }
}

internal interface IScopedCancellationTestTarget : ISystemTarget
{
    Task<int> RunAsync(Guid callId, CancellationToken cancellationToken);
    Task RunVoidAsync(Guid callId, CancellationToken cancellationToken);
}

internal sealed class ScopedCancellationTestTarget : SystemTarget, IScopedCancellationTestTarget, ILifecycleParticipant<ISiloLifecycle>
{
    internal static readonly GrainType TargetType = SystemTargetGrainId.CreateGrainType("scoped-cancellation-test");
    private readonly ConcurrentDictionary<Guid, Call> _calls = new();

    public ScopedCancellationTestTarget(SystemTargetShared shared) : base(TargetType, shared) =>
        shared.ActivationDirectory.RecordNewTarget(this);

    public void Participate(ISiloLifecycle lifecycle) { }

    internal Call AddCall()
    {
        var call = new Call();
        Assert.True(_calls.TryAdd(call.Id, call));
        return call;
    }

    internal void RemoveCall(Guid id) => Assert.True(_calls.TryRemove(id, out _));

    public async Task<int> RunAsync(Guid callId, CancellationToken cancellationToken)
    {
        var call = _calls[callId];
        try
        {
            using var registration = cancellationToken.Register(() => call.CancellationObserved.TrySetResult());
            call.Entered.SetResult();
            await call.Release.Task;
            return 42;
        }
        finally
        {
            call.Exited.TrySetResult();
        }
    }

    public async Task RunVoidAsync(Guid callId, CancellationToken cancellationToken) => await RunAsync(callId, cancellationToken);

    internal sealed class Call
    {
        public Guid Id { get; } = Guid.NewGuid();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
