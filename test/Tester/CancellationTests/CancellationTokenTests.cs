using Orleans.Configuration;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.CancellationTests;

/// <summary>
/// Tests for CancellationToken functionality with acknowledgement waiting enabled.
/// </summary>
public sealed class CancellationTokenTests_WaitForAcknowledgement(CancellationTokenTests_WaitForAcknowledgement.Fixture fixture) : CancellationTokenTests(fixture), IClassFixture<CancellationTokenTests_WaitForAcknowledgement.Fixture>
{
    public sealed class Fixture : FixtureBase
    {
        // Wait for callees to acknowledge cancellation.
        public override bool WaitForCancellationAcknowledgement => true;
    }
}

/// <summary>
/// Tests for CancellationToken functionality with acknowledgement waiting disabled.
/// </summary>
public sealed class CancellationTokenTests_NoWaitForAcknowledgement(CancellationTokenTests_NoWaitForAcknowledgement.Fixture fixture) : CancellationTokenTests(fixture), IClassFixture<CancellationTokenTests_NoWaitForAcknowledgement.Fixture>
{
    public sealed class Fixture : FixtureBase
    {
        // Do not wait for callees to acknowledge cancellation.
        public override bool WaitForCancellationAcknowledgement => false;
    }
}

/// <summary>
/// Base class for testing CancellationToken propagation and handling across grain calls.
/// </summary>
public abstract class CancellationTokenTests(CancellationTokenTests.FixtureBase fixture)
{
    public abstract class FixtureBase : BaseInProcessTestClusterFixture
    {
        public abstract bool WaitForCancellationAcknowledgement { get; }
        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            base.ConfigureTestCluster(builder);
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.Configure<SiloMessagingOptions>(options =>
                {
                    options.WaitForCancellationAcknowledgement = WaitForCancellationAcknowledgement;
                });
            });

            builder.ConfigureClient(clientBuilder =>
            {
                clientBuilder.Configure<ClientMessagingOptions>(options =>
                {
                    options.WaitForCancellationAcknowledgement = WaitForCancellationAcknowledgement;
                });
            });
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task GrainTaskCancellation(int delay)
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        var grainTask = grain.LongWait(cts.Token, TimeSpan.FromSeconds(10), callId);
        cts.CancelAfter(delay);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
        if (delay > 0)
        {
            await WaitForCallCancellation(grain, callId);
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task MultipleGrainsTaskCancellation(int delay)
    {
        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        var grains = Enumerable.Range(0, 5).Select(_ => fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid())).ToList();
        var grainTasks = grains.Select(grain =>
            Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                grain.LongWaitInterleaving(cts.Token, TimeSpan.FromSeconds(10), callId)))
            .ToList();
        cts.CancelAfter(delay);
        await Task.WhenAll(grainTasks);
        if (delay > 0)
        {
            foreach (var grain in grains)
            {
                await WaitForCallCancellation(grain, callId);
            }
        }
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task GrainTaskMultipleCancellations(int delay)
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
        var callIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var grainTasks = callIds
            .Select(async callId =>
            {
                using var cts = new CancellationTokenSource();
                var task = grain.LongWaitInterleaving(cts.Token, TimeSpan.FromSeconds(10), callId);
                cts.CancelAfter(delay);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            })
            .ToList();
        await Task.WhenAll(grainTasks);
        if (delay > 0)
        {
            await WaitForCallCancellation(grain, callIds);
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task TokenPassingWithoutCancellation_NoExceptionShouldBeThrown()
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        try
        {
            await grain.LongWait(cts.Token, TimeSpan.FromMilliseconds(1), Guid.Empty);
        }
        catch (Exception ex)
        {
            Assert.Fail("Expected no exception, but got: " + ex.Message);
        }
    }

    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task PreCancelledTokenPassing()
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Except a OperationCanceledException to be thrown as the token is already cancelled
        Assert.Throws<OperationCanceledException>(() => grain.LongWait(cts.Token, TimeSpan.FromSeconds(10), Guid.Empty).Ignore());
    }

    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task CancellationTokenCallbacksExecutionContext()
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        var grainTask = grain.CancellationTokenCallbackResolve(cts.Token, callId);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        if (fixture.WaitForCancellationAcknowledgement)
        {
            var result = await grainTask;
            Assert.True(result);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
        }

        await WaitForCallCancellation(grain, callId);
    }

    [Fact, TestCategory("BVT"), TestCategory("Cancellation")]
    public async Task CancellationTokenCallbacksTaskSchedulerContext()
    {
        var grains = await GetGrains<bool>(false);

        var callId = Guid.NewGuid();
        var grainTask = grains.Item1.CallOtherCancellationTokenCallbackResolve(grains.Item2, callId);
        if (fixture.WaitForCancellationAcknowledgement)
        {
            var result = await grainTask;
            Assert.True(result);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
        }

        await WaitForCallCancellation(grains.Item2, callId);
    }

    [Fact, TestCategory("Cancellation")]
    public async Task CancellationTokenCallbacksThrow_ExceptionDoesNotPropagate()
    {
        var grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        grain.CancellationTokenCallbackThrow(cts.Token, callId).Ignore();
        // Cancellation is a cooperative mechanism, so we don't expect the exception to propagate
        cts.CancelAfter(100);
        await WaitForCallCancellation(grain, callId);
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task InSiloCancellation(int delay)
    {
        await CancellationTestCore(false, delay);
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task InterSiloCancellation(int delay)
    {
        await CancellationTestCore(true, delay);
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task InterSiloClientCancellationTokenPassing(int delay)
    {
        await ClientCancellationTokenPassing(delay, true);
    }

    [Theory, TestCategory("BVT"), TestCategory("Cancellation")]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(300)]
    public async Task InSiloClientCancellationTokenPassing(int delay)
    {
        await ClientCancellationTokenPassing(delay, false);
    }

    private async Task ClientCancellationTokenPassing(int delay, bool interSilo)
    {
        var grains = await GetGrains<bool>(interSilo);
        var grain = grains.Item1;
        var target = grains.Item2;
        using var cts = new CancellationTokenSource();
        var callId = Guid.NewGuid();
        var grainTask = grain.CallOtherLongRunningTask(target, cts.Token, TimeSpan.FromSeconds(10), callId);
        cts.CancelAfter(delay);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
        if (delay > 0)
        {
            await WaitForCallCancellation(target, callId);
        }
    }

    private async Task CancellationTestCore(bool interSilo, int delay)
    {
        var grains = await GetGrains<bool>(interSilo);
        var grain = grains.Item1;
        var target = grains.Item2;
        var callId = Guid.NewGuid();
        var grainTask = grain.CallOtherLongRunningTaskWithLocalCancellation(target, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(delay), callId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grainTask);
        if (delay > 0)
        {
            await WaitForCallCancellation(target, callId);
        }
    }

    private async Task<Tuple<ILongRunningTaskGrain<T1>, ILongRunningTaskGrain<T1>>> GetGrains<T1>(bool placeOnDifferentSilos = true)
    {
        var attemptNumber = 0;
        var attemptLimit = 50;
        ILongRunningTaskGrain<T1> grain, target;
        string instanceId, targetInstanceId;
        do
        {
            if (attemptNumber > 0)
            {
                if (attemptNumber >= attemptLimit)
                {
                    throw new Exception("Could not make requested grains placement");
                }

                await Task.Delay(500);
            }

            ++attemptNumber;
            var firstSilo = fixture.HostedCluster.Silos.First().SiloAddress;
            RequestContext.Set(IPlacementDirector.PlacementHintKey, firstSilo);
            grain = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
            instanceId = await grain.GetRuntimeInstanceId();

            if (placeOnDifferentSilos)
            {
                var secondSilo = fixture.HostedCluster.Silos.Skip(1).First().SiloAddress;
                RequestContext.Set(IPlacementDirector.PlacementHintKey, secondSilo);
            }

            target = fixture.GrainFactory.GetGrain<ILongRunningTaskGrain<T1>>(Guid.NewGuid());
            targetInstanceId = await target.GetRuntimeInstanceId();
            RequestContext.Clear();
        } while (placeOnDifferentSilos && instanceId.Equals(targetInstanceId) || !placeOnDifferentSilos && !instanceId.Equals(targetInstanceId));

        return new Tuple<ILongRunningTaskGrain<T1>, ILongRunningTaskGrain<T1>>(grain, target);
    }

    private async Task WaitForCallCancellation<T>(ILongRunningTaskGrain<T> grain, Guid callId)
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(300));
        await foreach (var (cancelledCallId, error) in grain.WatchCancellations(cts.Token))
        {
            if (cancelledCallId == callId)
            {
                if (error is not null)
                {
                    throw new Exception("Expected no error, but found an error", error);
                }

                return;
            }
        }

        Assert.Fail("Did not encounter the expected call id");
    }

    private async Task WaitForCallCancellation<T>(ILongRunningTaskGrain<T> grain, Guid[] callIds)
    {
        using var cts = new CancellationTokenSource();
        var targetIds = new HashSet<Guid>(callIds);
        cts.CancelAfter(TimeSpan.FromSeconds(300));
        await foreach (var (cancelledCallId, error) in grain.WatchCancellations(cts.Token))
        {
            if (targetIds.Remove(cancelledCallId))
            {
                if (error is not null)
                {
                    throw new Exception("Expected no error, but found an error", error);
                }

                if (targetIds.Count == 0)
                {
                    return;
                }
            }
        }

        Assert.Fail("Did not encounter the expected call id");
    }
}