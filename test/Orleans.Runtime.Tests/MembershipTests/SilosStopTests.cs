using System.Net;
using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.MembershipTests
{
    /// <summary>
    /// Tests handling of ungraceful silo shutdowns and their impact on outstanding grain requests.
    /// </summary>
    [TestSuite("Functional")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class SilosStopTests : TestClusterPerTest
    {
        private class BuilderConfigurator : ISiloConfigurator, IClientBuilderConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .Configure<ClusterMembershipOptions>(options =>
                    {
                        options.NumMissedProbesLimit = 1;
                        options.NumVotesForDeathDeclaration = 1;
                        options.TableRefreshTimeout = TimeSpan.FromSeconds(2);
                    })
                    .Configure<SiloMessagingOptions>(options => options.AssumeHomogenousSilosForTesting = true);
            }

            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                var clusterOptions = configuration.GetTestClusterOptions();
                clientBuilder.UseStaticClustering(new IPEndPoint(IPAddress.Loopback, clusterOptions.BaseGatewayPort));
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddClientBuilderConfigurator<BuilderConfigurator>();
            builder.AddSiloBuilderConfigurator<BuilderConfigurator>();
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness")]
        public async Task SiloUngracefulShutdown_OutstandingRequestsBreak()
        {
            var grain = await GetGrainOnTargetSilo(HostedCluster.Primary!);
            Assert.NotNull(grain);
            var target = await GetGrainOnTargetSilo(HostedCluster.SecondarySilos[0]);
            Assert.NotNull(target);

            var promise = grain.CallOtherLongRunningTask(target, true, TimeSpan.FromSeconds(7));

            await Task.Delay(500, TestContext.Current.CancellationToken);
            await HostedCluster.KillSiloAsync(HostedCluster.SecondarySilos[0]);

            await Assert.ThrowsAsync<SiloUnavailableException>(() => promise);
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness")]
        public async Task SiloUngracefulShutdown_GatewayForwardedRequestBreaks()
        {
            var target = await GetGrainOnTargetSilo(HostedCluster.SecondarySilos[0]);
            Assert.NotNull(target);

            var observer = new LongRunningTaskObserver();
            var observerReference = GrainFactory.CreateObjectReference<ILongRunningTaskObserver>(observer);
            try
            {
                var callId = Guid.NewGuid();
                var promise = target.LongWaitWithStartNotification(
                    TimeSpan.FromMinutes(1),
                    callId,
                    observerReference,
                    CancellationToken.None);

                await observer.WaitForCallToStart(callId);
                Assert.False(promise.IsCompleted);

                await HostedCluster.KillSiloAsync(HostedCluster.SecondarySilos[0]);

                await Assert.ThrowsAsync<SiloUnavailableException>(
                    () => promise.WaitAsync(TimeSpan.FromSeconds(30)));
            }
            finally
            {
                GrainFactory.DeleteObjectReference<ILongRunningTaskObserver>(observerReference);
            }
        }

        private async Task<ILongRunningTaskGrain<bool>?> GetGrainOnTargetSilo(SiloHandle siloHandle)
        {
            const int maxRetry = 10;
            for (int i = 0; i < maxRetry; i++)
            {
                RequestContext.Set(IPlacementDirector.PlacementHintKey, siloHandle.SiloAddress);
                try
                {
                    var grain = GrainFactory.GetGrain<ILongRunningTaskGrain<bool>>(Guid.NewGuid());
                    var instanceId = await grain.GetRuntimeInstanceId();
                    if (instanceId.Contains(siloHandle.SiloAddress.Endpoint.ToString()))
                        return grain;
                }
                finally
                {
                    RequestContext.Remove(IPlacementDirector.PlacementHintKey);
                }

                await Task.Delay(100);
            }
            return null;
        }

        private sealed class LongRunningTaskObserver : ILongRunningTaskObserver
        {
            private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Guid _callId;

            public void OnCallStarted(Guid callId)
            {
                _callId = callId;
                _started.TrySetResult();
            }

            public async Task WaitForCallToStart(Guid callId)
            {
                await _started.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(callId, _callId);
            }
        }
    }
}
