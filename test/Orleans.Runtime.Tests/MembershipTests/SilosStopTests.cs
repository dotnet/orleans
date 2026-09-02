using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.TestingHost;
using Orleans.TestingHost.Diagnostics;
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
                clientBuilder
                    .Configure<ClientMessagingOptions>(options => options.DropExpiredMessages = false)
                    .UseStaticClustering(new IPEndPoint(IPAddress.Loopback, clusterOptions.BaseGatewayPort));
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
            await HostedCluster.KillSiloAsync(HostedCluster.SecondarySilos[0], TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<SiloUnavailableException>(() => promise);
        }

        [Fact, TestCategory("Liveness")]
        public async Task GatewayRequestSentBeforeSiloDeathIsRejected()
        {
            var runtimeClient = Client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
            var gateway = GetGateway();
            var previousResponseTimeout = runtimeClient.GetResponseTimeout();
            runtimeClient.SetResponseTimeout(TimeSpan.FromMinutes(1));
            try
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
                    Assert.Equal(1, gateway.TrackedRequestClientCount);

                    await HostedCluster.KillSiloAsync(
                        HostedCluster.SecondarySilos[0],
                        TestContext.Current.CancellationToken);

                    await Assert.ThrowsAsync<SiloUnavailableException>(
                        () => promise.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
                    Assert.Equal(0, gateway.TrackedRequestClientCount);
                }
                finally
                {
                    GrainFactory.DeleteObjectReference<ILongRunningTaskObserver>(observerReference);
                }
            }
            finally
            {
                runtimeClient.SetResponseTimeout(previousResponseTimeout);
            }
        }

        [Fact, TestCategory("Liveness")]
        public async Task CompletedGatewayForwardedRequestStopsTrackingClient()
        {
            var gateway = GetGateway();
            var target = await GetGrainOnTargetSilo(HostedCluster.SecondarySilos[0]);
            Assert.NotNull(target);

            var observer = new LongRunningTaskObserver();
            var observerReference = GrainFactory.CreateObjectReference<ILongRunningTaskObserver>(observer);
            try
            {
                var callId = Guid.NewGuid();
                var promise = target.LongWaitWithStartNotification(
                    TimeSpan.FromSeconds(5),
                    callId,
                    observerReference,
                    CancellationToken.None);

                await observer.WaitForCallToStart(callId);
                Assert.Equal(1, gateway.TrackedRequestClientCount);

                await promise;
                Assert.Equal(0, gateway.TrackedRequestClientCount);
            }
            finally
            {
                GrainFactory.DeleteObjectReference<ILongRunningTaskObserver>(observerReference);
            }
        }

        [Fact, TestCategory("Liveness")]
        public async Task GatewayForwardedRequestCancellationAndSiloDeathRaceClearsTracking()
        {
            var gateway = GetGateway();
            var targetSilo = HostedCluster.SecondarySilos[0];
            var target = await GetGrainOnTargetSilo(targetSilo);
            Assert.NotNull(target);

            var observer = new LongRunningTaskObserver();
            var observerReference = GrainFactory.CreateObjectReference<ILongRunningTaskObserver>(observer);
            try
            {
                using var cancellation = new CancellationTokenSource();
                var callId = Guid.NewGuid();
                var promise = target.LongWaitWithStartNotification(
                    TimeSpan.FromMinutes(1),
                    callId,
                    observerReference,
                    cancellation.Token);

                await observer.WaitForCallToStart(callId);
                Assert.Equal(1, gateway.TrackedRequestClientCount);

                var releaseRace = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancelTask = Task.Run(
                    async () =>
                    {
                        await releaseRace.Task;
                        await cancellation.CancelAsync();
                    },
                    TestContext.Current.CancellationToken);
                var killTask = Task.Run(
                    async () =>
                    {
                        await releaseRace.Task;
                        await HostedCluster.KillSiloAsync(targetSilo, TestContext.Current.CancellationToken);
                    },
                    TestContext.Current.CancellationToken);

                releaseRace.SetResult();
                await Task.WhenAll(cancelTask, killTask);

                var exception = await Record.ExceptionAsync(
                    () => promise.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
                Assert.True(
                    exception is OperationCanceledException or SiloUnavailableException,
                    $"Expected cancellation or dead-silo rejection, but received {exception?.GetType().FullName ?? "no exception"}: {exception}");
                await HostedCluster.WaitForLivenessToStabilizeAsync(didKill: true)
                    .WaitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, gateway.TrackedRequestClientCount);
            }
            finally
            {
                GrainFactory.DeleteObjectReference<ILongRunningTaskObserver>(observerReference);
            }
        }

        [Fact, TestCategory("Liveness")]
        public async Task ClientShutdownWithGatewayForwardedRequestClearsTracking()
        {
            var gateway = GetGateway();
            var target = await GetGrainOnTargetSilo(HostedCluster.SecondarySilos[0]);
            Assert.NotNull(target);

            var observer = new LongRunningTaskObserver();
            ILongRunningTaskObserver? observerReference = GrainFactory.CreateObjectReference<ILongRunningTaskObserver>(observer);
            try
            {
                var callId = Guid.NewGuid();
                var promise = target.LongWaitWithStartNotification(
                    TimeSpan.FromMinutes(1),
                    callId,
                    observerReference,
                    CancellationToken.None);

                await observer.WaitForCallToStart(callId);
                GrainFactory.DeleteObjectReference<ILongRunningTaskObserver>(observerReference);
                observerReference = null;
                Assert.Equal(1, gateway.TrackedRequestClientCount);

                await HostedCluster.StopClusterClientAsync(TestContext.Current.CancellationToken);

                await Assert.ThrowsAsync<SiloUnavailableException>(() => promise);
                Assert.Equal(0, gateway.TrackedRequestClientCount);
            }
            finally
            {
                if (observerReference is not null)
                {
                    GrainFactory.DeleteObjectReference<ILongRunningTaskObserver>(observerReference);
                }
            }
        }

        [Fact, TestCategory("Liveness")]
        public async Task GatewayDeadSiloRejectionClearsAmbientRequestContextForExternalClient()
        {
            const string contextKey = "gateway-rejection-sentinel";
            const string contextValue = "must-not-leak";
            var primaryServices = ((InProcessSiloHandle)HostedCluster.Primary!).ServiceProvider;
            var gateway = primaryServices.GetRequiredService<MessageCenter>().Gateway!;
            var messageFactory = primaryServices.GetRequiredService<MessageFactory>();
            var connectionManager = primaryServices.GetRequiredService<ConnectionManager>();
            var clientId = Assert.Single(((IConnectedClientCollection)gateway).GetConnectedClientIds());
            var targetSilo = HostedCluster.SecondarySilos[0].SiloAddress;
            var destination = await connectionManager.GetConnection(targetSilo);
            var request = new Message
            {
                Id = new CorrelationId(1),
                Direction = Message.Directions.Request,
                SendingSilo = HostedCluster.Primary!.SiloAddress,
                SendingGrain = clientId,
                TargetSilo = targetSilo,
                TargetGrain = GrainId.Create("target", Guid.NewGuid().ToString()),
            };
            Assert.True(gateway.TryGetClientState(request, out var client));

            await HostedCluster.KillSiloAsync(
                HostedCluster.SecondarySilos[0],
                TestContext.Current.CancellationToken);
            await HostedCluster.WaitForLivenessToStabilizeAsync(didKill: true)
                .WaitAsync(TestContext.Current.CancellationToken);

            using var gatewayEvents = new DiagnosticEventCollector(GatewayEvents.ListenerName);
            var rejectionEventTask = gatewayEvents.WaitForEventAsync(
                nameof(GatewayEvents.DeadSiloRequestRejected),
                diagnosticEvent => diagnosticEvent.Payload is GatewayEvents.DeadSiloRequestRejected rejected
                    && rejected.ClientId.Equals(clientId)
                    && rejected.Rejection.Id.Equals(request.Id),
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            RequestContext.Set(contextKey, contextValue);
            try
            {
                var unsanitizedRejection = messageFactory.CreateRejectionResponse(
                    request,
                    Message.RejectionTypes.Transient,
                    "Target silo became unavailable");
                Assert.Equal(contextValue, unsanitizedRejection.RequestContextData![contextKey]);

                client.SendRequest(request, destination);

                var diagnosticEvent = await rejectionEventTask;
                var rejected = Assert.IsType<GatewayEvents.DeadSiloRequestRejected>(diagnosticEvent.Payload);
                Assert.Equal(HostedCluster.Primary.SiloAddress, rejected.SiloAddress);
                Assert.Equal(clientId, rejected.ClientId);
                var rejection = rejected.Rejection;
                Assert.Equal(request.Id, rejection.Id);
                Assert.Equal(Message.Directions.Response, rejection.Direction);
                Assert.Equal(Message.ResponseTypes.Rejection, rejection.Result);
                Assert.Equal(clientId, rejection.TargetGrain);
                Assert.Null(rejection.RequestContextData);
                Assert.Equal(0, gateway.TrackedRequestClientCount);
                await Task.Yield();
                Assert.Single(
                    gatewayEvents.GetEvents(nameof(GatewayEvents.DeadSiloRequestRejected)),
                    diagnosticEvent => diagnosticEvent.Payload is GatewayEvents.DeadSiloRequestRejected duplicate
                        && duplicate.ClientId.Equals(clientId)
                        && duplicate.Rejection.Id.Equals(request.Id));
            }
            finally
            {
                RequestContext.Remove(contextKey);
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

        private Gateway GetGateway() =>
            ((InProcessSiloHandle)HostedCluster.Primary!).ServiceProvider.GetRequiredService<MessageCenter>().Gateway!;

        private sealed class LongRunningTaskObserver : ILongRunningTaskObserver
        {
            private readonly TaskCompletionSource<Guid> _startedCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void OnCallStarted(Guid callId) => _startedCall.TrySetResult(callId);

            public async Task WaitForCallToStart(Guid callId)
            {
                var startedCallId = await _startedCall.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(callId, startedCallId);
            }
        }
    }
}
