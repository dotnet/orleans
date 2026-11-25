using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.ConsistentRing;
using Orleans.Runtime.Scheduler;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT"), TestCategory("GrainServices")]
public class GrainServiceCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_CancelsInlineBeforeTransitioningToStopped(bool callbackThrows)
    {
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .UseOrleans(silo => silo.UseLocalhostClustering())
            .Build();
        var ring = Substitute.For<IConsistentRingProvider>();
        var service = new TestGrainService(ring, host.Services.GetRequiredService<SystemTargetShared>());
        using var shutdown = service.Shutdown;
        var failure = new InvalidOperationException("stop callback failed");
        var callbackCount = 0;
        int callbackThread = 0;
        TaskScheduler? callbackScheduler = null;
        IGrainContext? callbackContext = null;
        var callbackSawStopped = true;
        using var registration = shutdown.Token.Register(() =>
        {
            callbackCount++;
            callbackThread = Environment.CurrentManagedThreadId;
            callbackScheduler = TaskScheduler.Current;
            callbackContext = RuntimeContext.Current;
            callbackSawStopped = service.IsStopped;
            if (callbackThrows)
            {
                throw failure;
            }
        });

        await service.RunOrQueueTask(async () =>
        {
            var stoppingThread = Environment.CurrentManagedThreadId;
            var stoppingScheduler = TaskScheduler.Current;
            Assert.Same(service, RuntimeContext.Current);
            Assert.False(service.IsStopped);

            if (callbackThrows)
            {
                var exception = Assert.Throws<AggregateException>(() => { _ = service.Stop(); });
                Assert.Same(failure, Assert.Single(exception.InnerExceptions));
                Assert.False(service.IsStopped);
                ring.DidNotReceive().UnSubscribeFromRangeChangeEvents(service);
            }
            else
            {
                var stopping = service.Stop();
                Assert.True(stopping.IsCompletedSuccessfully);
                Assert.True(service.IsStopped);
            }

            Assert.True(shutdown.IsCancellationRequested);
            Assert.Equal(1, callbackCount);
            Assert.Equal(stoppingThread, callbackThread);
            Assert.Same(stoppingScheduler, callbackScheduler);
            Assert.Same(service, callbackContext);
            Assert.False(callbackSawStopped);

            await service.Stop();
            await service.Stop();
            Assert.True(service.IsStopped);
            Assert.Equal(1, callbackCount);
            ring.Received(1).UnSubscribeFromRangeChangeEvents(service);
        }).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    private sealed class TestGrainService(IConsistentRingProvider ring, SystemTargetShared shared)
        : GrainService(GrainId.Create("test-stop", "scheduler"), ring, shared)
    {
        public CancellationTokenSource Shutdown => StoppedCancellationTokenSource;
        public bool IsStopped => Status == GrainServiceStatus.Stopped;
    }
}
