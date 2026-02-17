#nullable enable
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.CancellationTests;

/// <summary>
/// Tests for SystemTarget CancellationToken functionality with acknowledgement waiting enabled.
/// </summary>
public sealed class SystemTargetCancellationTokenTests_WaitForAcknowledgement(SystemTargetCancellationTokenTests_WaitForAcknowledgement.Fixture fixture)
    : SystemTargetCancellationTokenTests(fixture), IClassFixture<SystemTargetCancellationTokenTests_WaitForAcknowledgement.Fixture>
{
    public sealed class Fixture : FixtureBase
    {
        public override bool WaitForCancellationAcknowledgement => true;
    }
}

/// <summary>
/// Tests for SystemTarget CancellationToken functionality with acknowledgement waiting disabled.
/// </summary>
public sealed class SystemTargetCancellationTokenTests_NoWaitForAcknowledgement(SystemTargetCancellationTokenTests_NoWaitForAcknowledgement.Fixture fixture)
    : SystemTargetCancellationTokenTests(fixture), IClassFixture<SystemTargetCancellationTokenTests_NoWaitForAcknowledgement.Fixture>
{
    public sealed class Fixture : FixtureBase
    {
        public override bool WaitForCancellationAcknowledgement => false;
    }
}

/// <summary>
/// Base class for testing CancellationToken propagation and handling for SystemTargets.
/// Note: All SystemTarget calls are interleaving by default - they execute immediately
/// without queueing, unlike regular grain calls.
/// </summary>
public abstract class SystemTargetCancellationTokenTests(SystemTargetCancellationTokenTests.FixtureBase fixture)
{
    /// <summary>
    /// The grain type for the cancellation test system target.
    /// </summary>
    public static readonly GrainType CancellationTestSystemTargetType = SystemTargetGrainId.CreateGrainType("cancellation-test-target");

    public abstract class FixtureBase : BaseInProcessTestClusterFixture
    {
        public abstract bool WaitForCancellationAcknowledgement { get; }

        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            base.ConfigureTestCluster(builder);
            builder.ConfigureHost(hostBuilder => hostBuilder.Logging.SetMinimumLevel(LogLevel.Debug));
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.Configure<SiloMessagingOptions>(opts =>
                {
                    opts.WaitForCancellationAcknowledgement = WaitForCancellationAcknowledgement;
                });

                // Register the CancellationTestSystemTarget as a lifecycle participant
                siloBuilder.Services.AddSingleton<CancellationTestSystemTarget>();
                siloBuilder.Services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(sp => sp.GetRequiredService<CancellationTestSystemTarget>());
            });

            builder.ConfigureClient(clientBuilder =>
            {
                clientBuilder.Configure<ClientMessagingOptions>(opts =>
                {
                    opts.WaitForCancellationAcknowledgement = WaitForCancellationAcknowledgement;
                });
            });
        }

        /// <summary>
        /// Gets a reference to the CancellationTestSystemTarget on the specified silo.
        /// </summary>
        public ICancellationTestSystemTarget GetSystemTarget(SiloAddress siloAddress)
        {
            return ((IInternalGrainFactory)GrainFactory).GetSystemTarget<ICancellationTestSystemTarget>(CancellationTestSystemTargetType, siloAddress);
        }
    }

    public bool WaitForCancellationAcknowledgement => fixture.WaitForCancellationAcknowledgement;

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task SystemTargetTaskCancellation(int delay)
    {
        var siloAddress = fixture.HostedCluster.Silos.First().SiloAddress;
        var systemTarget = fixture.GetSystemTarget(siloAddress);

        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        var task = systemTarget.LongWait(cts.Token, TimeSpan.FromSeconds(10), callId);
        cts.CancelAfter(delay);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        if (delay > 0)
        {
            await WaitForCallCancellation(systemTarget, callId);
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task MultipleSystemTargetsTaskCancellation(int delay)
    {
        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        var systemTargets = fixture.HostedCluster.Silos
            .Select(s => fixture.GetSystemTarget(s.SiloAddress))
            .ToList();

        var tasks = systemTargets.Select(st =>
            Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                st.LongWait(cts.Token, TimeSpan.FromSeconds(10), callId)))
            .ToList();

        cts.CancelAfter(delay);
        await Task.WhenAll(tasks);
        if (delay > 0)
        {
            foreach (var st in systemTargets)
            {
                await WaitForCallCancellation(st, callId);
            }
        }
    }

    /// <summary>
    /// Tests multiple concurrent cancellations on the same SystemTarget.
    /// Since all SystemTarget calls are interleaving, they all run concurrently.
    /// </summary>
    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task SystemTargetMultipleConcurrentCancellations(int delay)
    {
        var siloAddress = fixture.HostedCluster.Silos.First().SiloAddress;
        var systemTarget = fixture.GetSystemTarget(siloAddress);

        var callIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var tasks = callIds
            .Select(async callId =>
            {
                using var cts = new CancellationTokenSource();
                // All these calls run concurrently since SystemTarget calls are interleaving
                var task = systemTarget.LongWait(cts.Token, TimeSpan.FromSeconds(10), callId);
                cts.CancelAfter(delay);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            })
            .ToList();

        await Task.WhenAll(tasks);
        if (delay > 0)
        {
            foreach (var callId in callIds)
            {
                await WaitForCallCancellation(systemTarget, callId);
            }
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task TokenPassingWithoutCancellation_NoExceptionShouldBeThrown()
    {
        var siloAddress = fixture.HostedCluster.Silos.First().SiloAddress;
        var systemTarget = fixture.GetSystemTarget(siloAddress);

        using var cts = new CancellationTokenSource();
        try
        {
            await systemTarget.LongWait(cts.Token, TimeSpan.FromMilliseconds(1), Guid.Empty);
        }
        catch (Exception ex)
        {
            Assert.Fail("Expected no exception, but got: " + ex.Message);
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task PreCancelledTokenPassing()
    {
        var siloAddress = fixture.HostedCluster.Silos.First().SiloAddress;
        var systemTarget = fixture.GetSystemTarget(siloAddress);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Expect an OperationCanceledException to be thrown as the token is already cancelled
        Assert.Throws<OperationCanceledException>(() =>
            systemTarget.LongWait(cts.Token, TimeSpan.FromSeconds(10), Guid.Empty).Ignore());
    }

    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task CancellationTokenCallbacksExecutionContext()
    {
        var siloAddress = fixture.HostedCluster.Silos.First().SiloAddress;
        var systemTarget = fixture.GetSystemTarget(siloAddress);

        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        var task = systemTarget.CancellationTokenCallbackResolve(cts.Token, callId);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        if (WaitForCancellationAcknowledgement)
        {
            var result = await task;
            Assert.True(result);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        await WaitForCallCancellation(systemTarget, callId);
    }

    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task CancellationTokenCallbacksTaskSchedulerContext()
    {
        var silos = fixture.HostedCluster.Silos.ToArray();
        var sourceTarget = fixture.GetSystemTarget(silos[0].SiloAddress);
        var destinationTarget = fixture.GetSystemTarget(silos.Length > 1 ? silos[1].SiloAddress : silos[0].SiloAddress);

        var callId = Guid.NewGuid();
        var task = sourceTarget.CallOtherCancellationTokenCallbackResolve(destinationTarget, callId);

        if (WaitForCancellationAcknowledgement)
        {
            var result = await task;
            Assert.True(result);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        await WaitForCallCancellation(destinationTarget, callId);
    }

    [Fact, TestCategory("Cancellation")]
    public async Task CancellationTokenCallbacksThrow_ExceptionDoesNotPropagate()
    {
        var siloAddress = fixture.HostedCluster.Silos.First().SiloAddress;
        var systemTarget = fixture.GetSystemTarget(siloAddress);

        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        systemTarget.CancellationTokenCallbackThrow(cts.Token, callId).Ignore();
        // Cancellation is a cooperative mechanism, so we don't expect the exception to propagate
        cts.CancelAfter(100);
        await WaitForCallCancellation(systemTarget, callId);
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task InSiloCancellation(int delay)
    {
        var siloAddress = fixture.HostedCluster.Silos.First().SiloAddress;
        await CancellationTestCore(siloAddress, siloAddress, delay);
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task InterSiloCancellation(int delay)
    {
        var silos = fixture.HostedCluster.Silos.ToArray();
        if (silos.Length < 2)
        {
            // Skip test if there's only one silo
            return;
        }

        await CancellationTestCore(silos[0].SiloAddress, silos[1].SiloAddress, delay);
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task InterSiloClientCancellationTokenPassing(int delay)
    {
        var silos = fixture.HostedCluster.Silos.ToArray();
        if (silos.Length < 2)
        {
            // Skip test if there's only one silo
            return;
        }

        await ClientCancellationTokenPassing(delay, silos[0].SiloAddress, silos[1].SiloAddress);
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task InSiloClientCancellationTokenPassing(int delay)
    {
        var siloAddress = fixture.HostedCluster.Silos.First().SiloAddress;
        await ClientCancellationTokenPassing(delay, siloAddress, siloAddress);
    }

    private async Task ClientCancellationTokenPassing(int delay, SiloAddress sourceAddress, SiloAddress targetAddress)
    {
        var sourceTarget = fixture.GetSystemTarget(sourceAddress);
        var target = fixture.GetSystemTarget(targetAddress);

        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        var task = sourceTarget.CallOtherLongRunningTask(target, cts.Token, TimeSpan.FromSeconds(10), callId);
        cts.CancelAfter(delay);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        if (delay > 0)
        {
            await WaitForCallCancellation(target, callId);
        }
    }

    private async Task CancellationTestCore(SiloAddress sourceAddress, SiloAddress targetAddress, int delay)
    {
        var sourceTarget = fixture.GetSystemTarget(sourceAddress);
        var target = fixture.GetSystemTarget(targetAddress);

        var callId = Guid.NewGuid();
        var task = sourceTarget.CallOtherLongRunningTaskWithLocalCancellation(target, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(delay), callId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        if (delay > 0)
        {
            await WaitForCallCancellation(target, callId);
        }
    }

    private static async Task WaitForCallCancellation(ICancellationTestSystemTarget systemTarget, Guid callId)
    {
        var (wasCancelled, error) = await systemTarget.WaitForCancellation(callId, TimeSpan.FromSeconds(30));
        if (!wasCancelled)
        {
            Assert.Fail($"Did not encounter the expected call id {callId}");
        }

        if (error is not null)
        {
            throw new Exception("Expected no error, but found an error", error);
        }
    }

    /// <summary>
    /// Tests that multiple concurrent requests can each be cancelled independently.
    /// Since all SystemTarget calls are interleaving, they all execute concurrently.
    /// </summary>
    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task MultipleConcurrentSystemTargetRequestsCancellation()
    {
        var siloAddress = fixture.HostedCluster.Silos.First().SiloAddress;
        var systemTarget = fixture.GetSystemTarget(siloAddress);

        // Start multiple concurrent requests (all SystemTarget calls are interleaving)
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();
        using var cts3 = new CancellationTokenSource();

        var callId1 = Guid.NewGuid();
        var callId2 = Guid.NewGuid();
        var callId3 = Guid.NewGuid();

        var task1 = systemTarget.LongWait(cts1.Token, TimeSpan.FromSeconds(10), callId1);
        var task2 = systemTarget.LongWait(cts2.Token, TimeSpan.FromSeconds(10), callId2);
        var task3 = systemTarget.LongWait(cts3.Token, TimeSpan.FromSeconds(10), callId3);

        // Wait for all to be running
        await Task.Delay(100);

        // Cancel only the second request
        await cts2.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task2);
        await WaitForCallCancellation(systemTarget, callId2);

        // First and third should still be running, cancel them
        await cts1.CancelAsync();
        await cts3.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task3);

        await WaitForCallCancellation(systemTarget, callId1);
        await WaitForCallCancellation(systemTarget, callId3);
    }
}

/// <summary>
/// A SystemTarget implementation for testing CancellationToken functionality.
/// Note: All SystemTarget calls are interleaving - they execute immediately without queueing.
/// </summary>
internal sealed class CancellationTestSystemTarget : SystemTarget, ICancellationTestSystemTarget, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly ConcurrentDictionary<Guid, (TaskCompletionSource<bool> Tcs, Exception? Error)> _cancelledCalls = new();
    private readonly ILocalSiloDetails _localSiloDetails;

    public CancellationTestSystemTarget(
        ILocalSiloDetails localSiloDetails,
        SystemTargetShared shared)
        : base(SystemTargetCancellationTokenTests.CancellationTestSystemTargetType, shared)
    {
        _localSiloDetails = localSiloDetails;
        // Register with the activation directory so it can receive messages
        shared.ActivationDirectory.RecordNewTarget(this);
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        // No lifecycle registration needed - we register in the constructor
    }

    /// <inheritdoc />
    public Task<string> GetRuntimeInstanceId()
    {
        return Task.FromResult(_localSiloDetails.SiloAddress.ToString());
    }

    /// <inheritdoc />
    public async Task LongWait(CancellationToken cancellationToken, TimeSpan delay, Guid callId)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RecordCancellation(callId, null);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task CallOtherLongRunningTask(ICancellationTestSystemTarget target, CancellationToken cancellationToken, TimeSpan delay, Guid callId)
    {
        await target.LongWait(cancellationToken, delay, callId);
    }

    /// <inheritdoc />
    public async Task CallOtherLongRunningTaskWithLocalCancellation(ICancellationTestSystemTarget target, TimeSpan delay, TimeSpan delayBeforeCancel, Guid callId)
    {
        using var cts = new CancellationTokenSource();
        var task = target.LongWait(cts.Token, delay, callId);
        cts.CancelAfter(delayBeforeCancel);
        await task;
    }

    /// <inheritdoc />
    public Task<bool> CancellationTokenCallbackResolve(CancellationToken cancellationToken, Guid callId)
    {
        var tcs = new TaskCompletionSource<bool>();
        var orleansTs = TaskScheduler.Current;
        cancellationToken.Register(() =>
        {
            if (TaskScheduler.Current != orleansTs)
            {
                var exception = new Exception("Callback executed on wrong thread");
                RecordCancellation(callId, exception);
                tcs.SetException(exception);
            }
            else
            {
                RecordCancellation(callId, null);
                tcs.SetResult(true);
            }
        });

        return tcs.Task;
    }

    /// <inheritdoc />
    public async Task<bool> CallOtherCancellationTokenCallbackResolve(ICancellationTestSystemTarget target, Guid callId)
    {
        using var cts = new CancellationTokenSource();
        var task = target.CancellationTokenCallbackResolve(cts.Token, callId);
        cts.CancelAfter(300);
        return await task;
    }

    /// <inheritdoc />
    public async Task CancellationTokenCallbackThrow(CancellationToken cancellationToken, Guid callId)
    {
        cancellationToken.Register(() =>
        {
            RecordCancellation(callId, null);
            throw new InvalidOperationException("From cancellation token callback");
        });

        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> WasCallCancelled(Guid callId)
    {
        return Task.FromResult(_cancelledCalls.ContainsKey(callId));
    }

    /// <inheritdoc />
    public async Task<(bool WasCancelled, Exception? Error)> WaitForCancellation(Guid callId, TimeSpan timeout)
    {
        var entry = _cancelledCalls.GetOrAdd(callId, _ => (new TaskCompletionSource<bool>(), null));

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await entry.Tcs.Task.WaitAsync(cts.Token);
            return (true, _cancelledCalls.TryGetValue(callId, out var result) ? result.Error : null);
        }
        catch (OperationCanceledException)
        {
            return (false, null);
        }
    }

    private readonly object _cancelledCallsLock = new();

    private void RecordCancellation(Guid callId, Exception? error)
    {
        var entry = _cancelledCalls.GetOrAdd(callId, _ => (new TaskCompletionSource<bool>(), error));
        if (error is not null)
        {
            lock (_cancelledCallsLock)
            {
                // Re-check inside the lock to ensure atomicity
                var currentEntry = _cancelledCalls.GetOrAdd(callId, _ => (entry.Tcs, error));
                if (currentEntry.Error is null)
                {
                    _cancelledCalls[callId] = (entry.Tcs, error);
                }
            }
        }

        entry.Tcs.TrySetResult(true);
    }
}
