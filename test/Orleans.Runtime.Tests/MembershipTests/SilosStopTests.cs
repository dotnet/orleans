using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Connections;
using Orleans.Connections.Transport;
using Orleans.Configuration;
using Orleans.Core.Diagnostics;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Placement;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Invocation;
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
                    .Configure<SiloMessagingOptions>(options => options.AssumeHomogenousSilosForTesting = true)
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<FaultingClientDirectory>();
                        services.AddSingleton<ILocalClientDirectory>(services => services.GetRequiredService<FaultingClientDirectory>());
                    });
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
                    await HostedCluster.WaitForLivenessToStabilizeAsync(didKill: true)
                        .WaitAsync(TestContext.Current.CancellationToken);

                    await Assert.ThrowsAsync<SiloUnavailableException>(
                        () => promise.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
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
            var targetSilo = HostedCluster.SecondarySilos[0];
            var target = await GetGrainOnTargetSilo(targetSilo);
            Assert.NotNull(target);
            var clientId = Assert.Single(((IConnectedClientCollection)gateway).GetConnectedClientIds());
            using var gatewayEvents = new DiagnosticEventCollector(GatewayEvents.ListenerName);
            var trackingStoppedTask = gatewayEvents.WaitForEventAsync(
                nameof(GatewayEvents.RequestTrackingStopped),
                diagnosticEvent => diagnosticEvent.Payload is GatewayEvents.RequestTrackingStopped stopped
                    && stopped.SiloAddress.Equals(HostedCluster.Primary!.SiloAddress)
                    && stopped.ClientId.Equals(clientId),
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            var instanceId = await target.GetRuntimeInstanceId();

            Assert.Contains(targetSilo.SiloAddress.Endpoint.ToString(), instanceId);
            await trackingStoppedTask;
            Assert.Equal(0, gateway.TrackedRequestClientCount);
        }

        [Fact, TestCategory("Liveness")]
        public async Task ResponseAddressingFailureQueuesForDisconnectedClient()
        {
            var services = ((InProcessSiloHandle)HostedCluster.Primary!).ServiceProvider;
            var messageCenter = services.GetRequiredService<MessageCenter>();
            var gateway = messageCenter.Gateway!;
            var clientId = Assert.Single(((IConnectedClientCollection)gateway).GetConnectedClientIds());
            var request = new Message
            {
                Id = new CorrelationId(-1),
                Direction = Message.Directions.Request,
                SendingSilo = HostedCluster.Primary.SiloAddress,
                SendingGrain = clientId,
                TargetSilo = HostedCluster.SecondarySilos[0].SiloAddress,
                TargetGrain = GrainId.Create("target", Guid.NewGuid().ToString()),
            };
            Assert.True(gateway.TryGetClientState(request, out var client));
            var connection = Assert.IsType<GatewayInboundConnection>(client.Connection);
            var response = services.GetRequiredService<MessageFactory>().CreateResponseMessage(request);
            response.GatewayRequestAttempt = 42;
            response.Result = Message.ResponseTypes.Success;
            var body = response.BodyObject = Response.Completed;
            var directory = services.GetRequiredService<FaultingClientDirectory>();
            client.RecordDisconnection();
            directory.UnavailableClient = clientId;
            try
            {
                await messageCenter.ReaddressResponse(response, HostedCluster.Primary.SiloAddress);

                Assert.Equal(1, directory.Failures);
                Assert.Null(client.Connection);
                Assert.Equal(Message.ResponseTypes.Success, response.Result);
                Assert.Same(body, response.BodyObject);
                Assert.Equal(clientId, response.TargetGrain);
                Assert.Null(response.TargetSilo);
                Assert.Equal(0, response.GatewayRequestAttempt);
                Assert.Null(response.GatewayResponseRoutingHistory);
                Assert.Equal(0, gateway.TrackedRequestClientCount);
            }
            finally
            {
                directory.UnavailableClient = default;
                client.RecordConnection(connection);
            }
        }

        [Theory, TestCategory("Liveness")]
        [InlineData((int)Message.ResponseTypes.Success, false, false)]
        [InlineData((int)Message.ResponseTypes.Success, true, false)]
        [InlineData((int)Message.ResponseTypes.Error, false, false)]
        [InlineData((int)Message.ResponseTypes.Error, true, false)]
        [InlineData((int)Message.ResponseTypes.Rejection, false, false)]
        [InlineData((int)Message.ResponseTypes.Rejection, true, false)]
        [InlineData((int)Message.ResponseTypes.Success, false, true)]
        [InlineData((int)Message.ResponseTypes.Success, true, true)]
        [InlineData((int)Message.ResponseTypes.Error, false, true)]
        [InlineData((int)Message.ResponseTypes.Error, true, true)]
        [InlineData((int)Message.ResponseTypes.Rejection, false, true)]
        [InlineData((int)Message.ResponseTypes.Rejection, true, true)]
        public async Task GatewayConnectionFailureQueuesOriginalBufferedTerminalResponse(
            int responseType,
            bool failureAlreadyCompleted,
            bool allowResponseReaddress)
        {
            var services = ((InProcessSiloHandle)HostedCluster.Primary!).ServiceProvider;
            var messageCenter = services.GetRequiredService<MessageCenter>();
            var gateway = messageCenter.Gateway!;
            var clientId = Assert.Single(((IConnectedClientCollection)gateway).GetConnectedClientIds());
            var replacementGateway = HostedCluster.SecondarySilos[0].SiloAddress;
            var request = new Message
            {
                Id = new CorrelationId(-4),
                Direction = Message.Directions.Request,
                SendingSilo = HostedCluster.Primary.SiloAddress,
                SendingGrain = clientId,
                TargetSilo = replacementGateway,
                TargetGrain = GrainId.Create("target", Guid.NewGuid().ToString()),
            };
            Assert.True(gateway.TryGetClientState(request, out var client));
            var connection = Assert.IsType<GatewayInboundConnection>(client.Connection);
            var pendingResponses = (ConcurrentQueue<Message>)typeof(Gateway.ClientState)
                .GetField("_pendingToSend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            using var shared = ActivatorUtilities.CreateInstance<MessageHandlerShared>(services);
            using var response = services.GetRequiredService<MessageFactory>().CreateResponseMessage(request);
            response.TargetSilo = replacementGateway;
            response.GatewayRequestAttempt = -42;
            response.GatewayResponseRoutingHistory = [HostedCluster.Primary.SiloAddress];
            response.Result = (Message.ResponseTypes)responseType;
            using var buffer = new ArcBufferWriter();
            buffer.Write([1, 2, 3]);
            var readRequest = shared.GetReceiveMessageHandler();
            readRequest.Body = buffer.ConsumeSlice(3);
            typeof(MessageReadRequest).GetField("_bodyLength", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(readRequest, 3);
            response.SetMessageReadRequest(readRequest);
            var acquisition = new TaskCompletionSource<Connection>(TaskCreationOptions.RunContinuationsAsynchronously);
            var failure = new ConnectionFailedException("Transient replacement gateway connection failure.");
            if (failureAlreadyCompleted)
            {
                acquisition.SetException(failure);
            }

            Task? sending = null;
            client.RecordDisconnection();
            try
            {
                Assert.Empty(pendingResponses);
                Assert.False(services.GetRequiredService<ISiloStatusOracle>().IsDeadSilo(replacementGateway));

                sending = messageCenter.SendMessageAsync(
                    new ValueTask<Connection>(acquisition.Task),
                    response,
                    sendMessage: null,
                    allowResponseReaddress);
                if (!failureAlreadyCompleted)
                {
                    Assert.False(sending.IsCompleted);
                    Assert.Empty(pendingResponses);
                    Assert.Same(readRequest, response._bodyObject);
                    acquisition.SetException(failure);
                }

                await sending.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

                Assert.Same(response, Assert.Single(pendingResponses));
                Assert.Equal((Message.ResponseTypes)responseType, response.Result);
                Assert.Equal(clientId, response.TargetGrain);
                Assert.Null(response.TargetSilo);
                Assert.Equal(0, response.GatewayRequestAttempt);
                Assert.Null(response.GatewayResponseRoutingHistory);
                Assert.Same(readRequest, response._bodyObject);
                Assert.Equal([1, 2, 3], readRequest.Body.ToArray());
                Assert.Equal(0, gateway.TrackedRequestClientCount);
                Assert.True(pendingResponses.TryDequeue(out var queued));
                Assert.Same(response, queued);
                response.Dispose();
                Assert.Null(response._bodyObject);
                Assert.Equal(0, readRequest.Body.Length);
                var first = shared.GetReceiveMessageHandler();
                var second = shared.GetReceiveMessageHandler();
                try
                {
                    Assert.Same(readRequest, first);
                    Assert.NotSame(first, second);
                }
                finally
                {
                    first.Reset();
                    second.Reset();
                }
            }
            finally
            {
                acquisition.TrySetException(failure);
                if (sending is not null)
                {
                    await sending.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                }

                if (pendingResponses.TryPeek(out var queued) && ReferenceEquals(queued, response))
                {
                    pendingResponses.TryDequeue(out _);
                }

                client.RecordConnection(connection);
            }
        }

        [Theory, TestCategory("Liveness")]
        [InlineData((int)Message.ResponseTypes.Status)]
        [InlineData((int)Message.ResponseTypes.Success)]
        [InlineData((int)Message.ResponseTypes.Error)]
        [InlineData((int)Message.ResponseTypes.Rejection)]
        public async Task SiloConnectionFailureDropsStatusAndQueuesBufferedTerminalResponse(int responseType)
        {
            var services = ((InProcessSiloHandle)HostedCluster.Primary!).ServiceProvider;
            var messageCenter = services.GetRequiredService<MessageCenter>();
            var gateway = messageCenter.Gateway!;
            var transport = Assert.IsType<SiloConnection>(
                await services.GetRequiredService<ConnectionManager>()
                    .GetConnection(HostedCluster.SecondarySilos[0].SiloAddress));
            var clientId = ClientGrainId.Create();
            var clients = (ConcurrentDictionary<ClientGrainId, Gateway.ClientState>)typeof(Gateway)
                .GetField("clients", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gateway)!;
            var unavailableGateway = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 0), 1);
            var request = new Message
            {
                Id = new CorrelationId(-5),
                Direction = Message.Directions.Request,
                SendingSilo = HostedCluster.Primary.SiloAddress,
                SendingGrain = clientId.GrainId,
                TargetSilo = HostedCluster.SecondarySilos[0].SiloAddress,
                TargetGrain = GrainId.Create("target", Guid.NewGuid().ToString()),
            };
            using var shared = ActivatorUtilities.CreateInstance<MessageHandlerShared>(services);
            using var response = services.GetRequiredService<MessageFactory>().CreateResponseMessage(request);
            response.Result = (Message.ResponseTypes)responseType;
            response.TargetSilo = unavailableGateway;
            response.GatewayRequestAttempt = -42;
            // Exhaust the routing budget so terminal fallback queues synchronously without a directory lookup.
            var history = Enumerable.Repeat(
                HostedCluster.Primary.SiloAddress,
                services.GetRequiredService<IOptions<SiloMessagingOptions>>().Value.MaxForwardCount).ToArray();
            response.GatewayResponseRoutingHistory = history;
            using var buffer = new ArcBufferWriter();
            buffer.Write([1, 2, 3]);
            var readRequest = shared.GetReceiveMessageHandler();
            readRequest.Body = buffer.ConsumeSlice(3);
            typeof(MessageReadRequest).GetField("_bodyLength", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(readRequest, 3);
            response.SetMessageReadRequest(readRequest);
            // This state has never had a connection, so its consumer cannot hold a stale live-connection snapshot.
            var client = new Gateway.ClientState(gateway, clientId);
            ConcurrentQueue<Message>? pendingResponses = null;
            try
            {
                pendingResponses = (ConcurrentQueue<Message>)typeof(Gateway.ClientState)
                    .GetField("_pendingToSend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
                Assert.True(clients.TryAdd(clientId, client));
                Assert.True(gateway.TryGetClientState(request, out var knownClient));
                Assert.Same(client, knownClient);
                Assert.Null(client.Connection);
                Assert.Empty(pendingResponses);
                Assert.False(MessageCenter.IsForwardedClientRequestUpdate(response));

                transport.FailMessage(response, "Gateway transport failed after all retries.");

                Assert.Equal((Message.ResponseTypes)responseType, response.Result);
                Assert.Equal(clientId.GrainId, response.TargetGrain);
                Assert.Null(client.Connection);
                Assert.Null(response.GatewayForwardingSource);
                Assert.Equal(0, gateway.TrackedRequestClientCount);
                if (response.Result == Message.ResponseTypes.Status)
                {
                    Assert.Empty(pendingResponses);
                    Assert.Equal(unavailableGateway, response.TargetSilo);
                    Assert.Equal(-42, response.GatewayRequestAttempt);
                    Assert.Same(history, response.GatewayResponseRoutingHistory);
                    Assert.Null(response._bodyObject);
                    Assert.Equal(0, readRequest.BodyLength);
                    Assert.Equal(0, readRequest.Body.Length);
                }
                else
                {
                    Assert.Same(response, Assert.Single(pendingResponses));
                    Assert.Null(response.TargetSilo);
                    Assert.Equal(0, response.GatewayRequestAttempt);
                    Assert.Null(response.GatewayResponseRoutingHistory);
                    Assert.Same(readRequest, response._bodyObject);
                    Assert.Equal(3, readRequest.BodyLength);
                    Assert.Equal([1, 2, 3], readRequest.Body.ToArray());
                    Assert.True(pendingResponses.TryDequeue(out var queued));
                    Assert.Same(response, queued);
                    response.Dispose();
                }

                var first = shared.GetReceiveMessageHandler();
                var second = shared.GetReceiveMessageHandler();
                try
                {
                    Assert.Same(readRequest, first);
                    Assert.NotSame(first, second);
                }
                finally
                {
                    first.Reset();
                    second.Reset();
                }
            }
            finally
            {
                clients.TryRemove(clientId, out _);
                if (pendingResponses is not null)
                {
                    while (pendingResponses.TryDequeue(out var queued))
                    {
                        queued.Dispose();
                    }
                }

                client.Drop();
            }
        }

        [Fact, TestCategory("Liveness")]
        public async Task MaxForwardCountRejectionStopsGatewayRequestTracking()
        {
            var gateway = GetGateway();
            var clientId = Assert.Single(((IConnectedClientCollection)gateway).GetConnectedClientIds());
            using var gatewayEvents = new DiagnosticEventCollector(GatewayEvents.ListenerName);
            var trackingStoppedTask = gatewayEvents.WaitForEventAsync(
                nameof(GatewayEvents.RequestTrackingStopped),
                diagnosticEvent => diagnosticEvent.Payload is GatewayEvents.RequestTrackingStopped stopped
                    && stopped.ClientId.Equals(clientId),
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            var grain = GrainFactory.GetGrain<IDeactivatingWhileActivatingTestGrain>(Random.Shared.Next());

            RequestContext.Set(
                IPlacementDirector.PlacementHintKey,
                HostedCluster.SecondarySilos[0].SiloAddress);
            try
            {
                var exception = await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => grain.DoSomething());
                Assert.StartsWith("Forwarding failed:", exception.Message);
            }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            }

            var trackingStopped = Assert.IsType<GatewayEvents.RequestTrackingStopped>(
                (await trackingStoppedTask).Payload);
            Assert.Equal(HostedCluster.Primary!.SiloAddress, trackingStopped.SiloAddress);
            Assert.Equal(clientId, trackingStopped.ClientId);
            Assert.Equal(0, gateway.TrackedRequestClientCount);
        }

        [Fact, TestCategory("Liveness")]
        public async Task GatewayForwardedRequestCancellationAndSiloDeathRaceClearsTracking()
        {
            var gateway = GetGateway();
            var clientId = Assert.Single(((IConnectedClientCollection)gateway).GetConnectedClientIds());
            var targetSilo = HostedCluster.SecondarySilos[0];
            var target = await GetGrainOnTargetSilo(targetSilo);
            Assert.NotNull(target);
            using var gatewayEvents = new DiagnosticEventCollector(GatewayEvents.ListenerName);

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
                gatewayEvents.Clear();
                var trackingStoppedTask = gatewayEvents.WaitForEventAsync(
                    nameof(GatewayEvents.RequestTrackingStopped),
                    diagnosticEvent => diagnosticEvent.Payload is GatewayEvents.RequestTrackingStopped stopped
                        && stopped.ClientId.Equals(clientId),
                    TimeSpan.FromSeconds(30),
                    TestContext.Current.CancellationToken);

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
                var trackingStopped = Assert.IsType<GatewayEvents.RequestTrackingStopped>(
                    (await trackingStoppedTask).Payload);
                Assert.Equal(HostedCluster.Primary!.SiloAddress, trackingStopped.SiloAddress);
                Assert.Equal(clientId, trackingStopped.ClientId);
                Assert.Equal(0, gateway.TrackedRequestClientCount);
                await HostedCluster.WaitForLivenessToStabilizeAsync(didKill: true)
                    .WaitAsync(TestContext.Current.CancellationToken);
                Assert.Single(
                    gatewayEvents.GetEvents(nameof(GatewayEvents.RequestTrackingStopped)),
                    diagnosticEvent => diagnosticEvent.Payload is GatewayEvents.RequestTrackingStopped stopped
                        && stopped.ClientId.Equals(clientId));
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

        [Fact, TestCategory("Liveness")]
        public async Task DeadSiloRejectionDoesNotStealSameIdRetry()
        {
            var primaryServices = ((InProcessSiloHandle)HostedCluster.Primary!).ServiceProvider;
            var gateway = primaryServices.GetRequiredService<MessageCenter>().Gateway!;
            var clientId = Assert.Single(((IConnectedClientCollection)gateway).GetConnectedClientIds());
            var deadSilo = HostedCluster.SecondarySilos[0];
            var destination = new RecordingConnection(primaryServices.GetRequiredService<ConnectionCommon>());
            var original = new Message
            {
                Id = new CorrelationId(2),
                Direction = Message.Directions.Request,
                SendingSilo = HostedCluster.Primary!.SiloAddress,
                SendingGrain = clientId,
                TargetSilo = deadSilo.SiloAddress,
                TargetGrain = GrainId.Create("target", Guid.NewGuid().ToString()),
            };
            var retry = new Message
            {
                Id = original.Id,
                Direction = Message.Directions.Request,
                SendingSilo = original.SendingSilo,
                SendingGrain = original.SendingGrain,
                TargetSilo = HostedCluster.Primary.SiloAddress,
                TargetGrain = GrainId.Create("target", Guid.NewGuid().ToString()),
            };
            Assert.True(gateway.TryGetClientState(original, out var client));

            await HostedCluster.KillSiloAsync(deadSilo, TestContext.Current.CancellationToken);
            await HostedCluster.WaitForLivenessToStabilizeAsync(didKill: true)
                .WaitAsync(TestContext.Current.CancellationToken);

            using var gatewayEvents = new DiagnosticEventCollector(GatewayEvents.ListenerName);
            var retryInserted = 0;
            var trackingStoppedTask = gatewayEvents.WaitForEventAsync(
                nameof(GatewayEvents.RequestTrackingStopped),
                diagnosticEvent =>
                {
                    if (diagnosticEvent.Payload is not GatewayEvents.RequestTrackingStopped stopped
                        || !stopped.ClientId.Equals(clientId))
                    {
                        return false;
                    }

                    if (Interlocked.Exchange(ref retryInserted, 1) == 0)
                    {
                        client.SendRequest(retry, destination);
                    }

                    return true;
                },
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            client.SendRequest(original, destination);

            await trackingStoppedTask;
            Assert.Equal(1, Volatile.Read(ref retryInserted));
            Assert.Same(retry, Assert.Single(destination.Messages));
            Assert.Equal(1, gateway.TrackedRequestClientCount);
            await Task.Yield();
            Assert.DoesNotContain(
                gatewayEvents.GetEvents(nameof(GatewayEvents.DeadSiloRequestRejected)),
                diagnosticEvent => diagnosticEvent.Payload is GatewayEvents.DeadSiloRequestRejected duplicate
                    && duplicate.ClientId.Equals(clientId)
                    && duplicate.Rejection.Id.Equals(original.Id));

            client.ClearPendingRequests();
            Assert.Equal(0, gateway.TrackedRequestClientCount);
        }

        [Theory, TestCategory("Liveness")]
        [InlineData("untracked")]
        [InlineData("tracked")]
        [InlineData("released")]
        [InlineData("superseded")]
        public void GatewayDeadSiloRejectionReturnsBufferedRequestOnce(string ownership)
        {
            var services = ((InProcessSiloHandle)HostedCluster.Primary!).ServiceProvider;
            var gateway = services.GetRequiredService<MessageCenter>().Gateway!;
            var clientId = Assert.Single(((IConnectedClientCollection)gateway).GetConnectedClientIds());
            var targetSilo = HostedCluster.SecondarySilos[0].SiloAddress;
            using var shared = ActivatorUtilities.CreateInstance<MessageHandlerShared>(services);
            using var request = new Message
            {
                Id = new CorrelationId(-3),
                Direction = Message.Directions.Request,
                SendingSilo = HostedCluster.Primary.SiloAddress,
                SendingGrain = clientId,
                TargetSilo = targetSilo,
                TargetGrain = GrainId.Create("target", Guid.NewGuid().ToString()),
            };
            using var buffer = new ArcBufferWriter();
            buffer.Write([1, 2, 3]);
            var readRequest = shared.GetReceiveMessageHandler();
            readRequest.Body = buffer.ConsumeSlice(3);
            typeof(MessageReadRequest).GetField("_bodyLength", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(readRequest, 3);
            request.SetMessageReadRequest(readRequest);
            Assert.True(gateway.TryGetClientState(request, out var client));
            var connection = Assert.IsType<GatewayInboundConnection>(client.Connection);
            try
            {
                if (ownership != "untracked")
                {
                    var destination = new RecordingConnection(services.GetRequiredService<ConnectionCommon>());
                    client.SendRequest(request, destination);
                    Assert.Same(request, Assert.Single(destination.Messages));
                    Assert.True(request.GatewayRequestAttempt > 0);
                }

                if (ownership == "released")
                {
                    client.RecordDisconnection();
                }
                else if (ownership == "superseded")
                {
                    client.ClearPendingRequests();
                }

                using var gatewayEvents = new DiagnosticEventCollector(GatewayEvents.ListenerName);

                client.RejectRequest(request, targetSilo);

                Assert.Null(request._bodyObject);
                Assert.Equal(0, readRequest.BodyLength);
                Assert.Equal(0, readRequest.Body.Length);
                var first = shared.GetReceiveMessageHandler();
                var second = shared.GetReceiveMessageHandler();
                try
                {
                    Assert.Same(readRequest, first);
                    Assert.NotSame(first, second);
                }
                finally
                {
                    first.Reset();
                    second.Reset();
                }

                var rejections = gatewayEvents.GetEvents(nameof(GatewayEvents.DeadSiloRequestRejected))
                    .Where(diagnosticEvent => diagnosticEvent.Payload is GatewayEvents.DeadSiloRequestRejected rejected
                        && rejected.ClientId.Equals(clientId)
                        && rejected.Rejection.Id.Equals(request.Id));
                if (ownership == "superseded")
                {
                    Assert.Empty(rejections);
                }
                else
                {
                    Assert.Single(rejections);
                }

                Assert.Equal(0, gateway.TrackedRequestClientCount);
            }
            finally
            {
                client.ClearPendingRequests();
                if (client.Connection is null)
                {
                    client.RecordConnection(connection);
                }
            }
        }

        [Theory, TestCategory("Liveness")]
        [InlineData("active")]
        [InlineData("rejected")]
        [InlineData("replaced")]
        [InlineData("completed")]
        [InlineData("disconnected")]
        [InlineData("reconnected")]
        [InlineData("cleared")]
        public async Task GatewayTransportRetryChecksAttemptOwnership(string ownership)
        {
            var services = ((InProcessSiloHandle)HostedCluster.Primary!).ServiceProvider;
            var gateway = services.GetRequiredService<MessageCenter>().Gateway!;
            var manager = services.GetRequiredService<ConnectionManager>();
            var transport = Assert.IsType<SiloConnection>(
                await manager.GetConnection(HostedCluster.SecondarySilos[0].SiloAddress));
            var clientId = Assert.Single(((IConnectedClientCollection)gateway).GetConnectedClientIds());
            var targetSilo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 0), 1);
            var destination = new RecordingConnection(services.GetRequiredService<ConnectionCommon>());
            var request = new Message
            {
                Id = new CorrelationId(-2),
                Direction = Message.Directions.Request,
                SendingSilo = HostedCluster.Primary.SiloAddress,
                SendingGrain = clientId,
                TargetSilo = targetSilo,
                TargetGrain = GrainId.Create("target", Guid.NewGuid().ToString()),
            };
            Assert.True(gateway.TryGetClientState(request, out var client));
            var connection = Assert.IsType<GatewayInboundConnection>(client.Connection);
            manager.OnConnected(targetSilo, destination);
            try
            {
                client.SendRequest(request, destination);
                Assert.Same(request, Assert.Single(destination.Messages));
                var attempt = request.GatewayRequestAttempt;
                Assert.True(attempt > 0);

                switch (ownership)
                {
                    case "rejected":
                        gateway.SiloStatusChangeNotification(targetSilo, SiloStatus.Dead);
                        break;
                    case "replaced":
                        client.SendRequest(new Message
                        {
                            Id = request.Id,
                            Direction = request.Direction,
                            SendingSilo = request.SendingSilo,
                            SendingGrain = request.SendingGrain,
                            TargetSilo = request.TargetSilo,
                            TargetGrain = request.TargetGrain,
                        }, destination);
                        break;
                    case "completed":
                        var response = services.GetRequiredService<MessageFactory>().CreateResponseMessage(request);
                        response.Result = Message.ResponseTypes.Success;
                        response.BodyObject = Response.Completed;
                        client.SendResponse(response);
                        break;
                    case "disconnected":
                    case "reconnected":
                        client.RecordDisconnection();
                        if (ownership == "reconnected")
                        {
                            client.RecordConnection(connection);
                        }
                        break;
                    case "cleared":
                        client.ClearPendingRequests();
                        break;
                }

                destination.Messages.Clear();
                typeof(SiloConnection).GetMethod("RetryMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(transport, [request, null]);

                Assert.Equal(1, request.RetryCount);
                if (ownership is "active" or "disconnected" or "reconnected")
                {
                    Assert.Same(request, Assert.Single(destination.Messages));
                    Assert.Equal(ownership == "active" ? attempt : -attempt, request.GatewayRequestAttempt);
                }
                else
                {
                    Assert.Empty(destination.Messages);
                }

                Assert.Equal(ownership is "active" or "replaced" ? 1 : 0, gateway.TrackedRequestClientCount);
            }
            finally
            {
                if (client.Connection is null)
                {
                    client.RecordConnection(connection);
                }

                client.ClearPendingRequests();
                manager.OnConnectionTerminated(targetSilo, destination, exception: null);
            }
        }

        [Fact, TestCategory("Liveness")]
        public async Task ForwardingUpdateToDeadSiloReplacesStaleDestinationTracking()
        {
            var primaryServices = ((InProcessSiloHandle)HostedCluster.Primary!).ServiceProvider;
            var messageCenter = primaryServices.GetRequiredService<MessageCenter>();
            var gateway = messageCenter.Gateway!;
            var clientId = Assert.Single(((IConnectedClientCollection)gateway).GetConnectedClientIds());
            var deadSilo = HostedCluster.SecondarySilos[0];
            var destination = new RecordingConnection(primaryServices.GetRequiredService<ConnectionCommon>());
            var original = new Message
            {
                Id = new CorrelationId(3),
                Direction = Message.Directions.Request,
                SendingSilo = HostedCluster.Primary!.SiloAddress,
                SendingGrain = clientId,
                TargetSilo = HostedCluster.Primary.SiloAddress,
                TargetGrain = GrainId.Create("target", Guid.NewGuid().ToString()),
            };
            var forwarded = new Message
            {
                Id = original.Id,
                Direction = Message.Directions.Request,
                SendingSilo = original.SendingSilo,
                SendingGrain = original.SendingGrain,
                TargetSilo = deadSilo.SiloAddress,
                TargetGrain = original.TargetGrain,
                ForwardCount = original.ForwardCount + 1,
            };
            var forwardingUpdate = new Message
            {
                Id = forwarded.Id,
                Direction = Message.Directions.Response,
                Result = Message.ResponseTypes.Status,
                TargetSilo = original.SendingSilo,
                TargetGrain = original.SendingGrain,
                SendingSilo = forwarded.TargetSilo,
                SendingGrain = forwarded.TargetGrain,
                BodyObject = new StatusResponse(isExecuting: false, isWaiting: false, diagnostics: []),
                GatewayForwardingSource = original.TargetSilo,
                ForwardCount = forwarded.ForwardCount,
            };
            Assert.True(gateway.TryGetClientState(original, out var client));
            client.SendRequest(original, destination);
            Assert.Same(original, Assert.Single(destination.Messages));
            Assert.True(original.GatewayRequestAttempt > 0);
            forwardingUpdate.GatewayRequestAttempt = original.GatewayRequestAttempt;
            Assert.Equal(1, gateway.TrackedRequestClientCount);

            await HostedCluster.KillSiloAsync(deadSilo, TestContext.Current.CancellationToken);
            await HostedCluster.WaitForLivenessToStabilizeAsync(didKill: true)
                .WaitAsync(TestContext.Current.CancellationToken);

            using var gatewayEvents = new DiagnosticEventCollector(GatewayEvents.ListenerName);
            var rejectionTask = gatewayEvents.WaitForEventAsync(
                nameof(GatewayEvents.DeadSiloRequestRejected),
                diagnosticEvent => diagnosticEvent.Payload is GatewayEvents.DeadSiloRequestRejected rejected
                    && rejected.ClientId.Equals(clientId)
                    && rejected.Rejection.Id.Equals(forwarded.Id),
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            messageCenter.ReceiveMessage(forwardingUpdate);

            var rejected = Assert.IsType<GatewayEvents.DeadSiloRequestRejected>((await rejectionTask).Payload);
            Assert.Null(forwardingUpdate._bodyObject);
            Assert.Equal(forwarded.Id, rejected.Rejection.Id);
            Assert.Equal(Message.Directions.Response, rejected.Rejection.Direction);
            Assert.Equal(Message.ResponseTypes.Rejection, rejected.Rejection.Result);
            Assert.Equal(deadSilo.SiloAddress, rejected.Rejection.SendingSilo);
            Assert.Equal(clientId, rejected.Rejection.TargetGrain);
            Assert.Null(rejected.Rejection.TargetSilo);
            Assert.Equal(0, rejected.Rejection.GatewayRequestAttempt);
            Assert.Null(rejected.Rejection.GatewayForwardingSource);
            Assert.Equal(0, gateway.TrackedRequestClientCount);
        }

        [Fact, TestCategory("Liveness")]
        public async Task ThrowingDiagnosticObserverDoesNotInterruptDeadSiloRejection()
        {
            var primaryServices = ((InProcessSiloHandle)HostedCluster.Primary!).ServiceProvider;
            var gateway = primaryServices.GetRequiredService<MessageCenter>().Gateway!;
            var clientId = Assert.Single(((IConnectedClientCollection)gateway).GetConnectedClientIds());
            var deadSilo = HostedCluster.SecondarySilos[0];
            var request = new Message
            {
                Id = new CorrelationId(4),
                Direction = Message.Directions.Request,
                SendingSilo = HostedCluster.Primary!.SiloAddress,
                SendingGrain = clientId,
                TargetSilo = deadSilo.SiloAddress,
                TargetGrain = GrainId.Create("target", Guid.NewGuid().ToString()),
            };
            Assert.True(gateway.TryGetClientState(request, out var client));

            await HostedCluster.KillSiloAsync(deadSilo, TestContext.Current.CancellationToken);
            await HostedCluster.WaitForLivenessToStabilizeAsync(didKill: true)
                .WaitAsync(TestContext.Current.CancellationToken);

            using var subscription = GatewayEvents.AllEvents.Subscribe(new ThrowingGatewayEventObserver());

            client.SendRequest(request, new RecordingConnection(primaryServices.GetRequiredService<ConnectionCommon>()));

            Assert.Equal(0, gateway.TrackedRequestClientCount);
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

        private sealed class FaultingClientDirectory(ClientDirectory inner) : ILocalClientDirectory
        {
            private readonly AsyncLocal<GrainId> _unavailableClient = new();
            private int _failures;

            public GrainId UnavailableClient
            {
                get => _unavailableClient.Value;
                set => _unavailableClient.Value = value;
            }

            public int Failures => Volatile.Read(ref _failures);

            public bool TryLocalLookup(GrainId grainId, [NotNullWhen(true)] out List<GrainAddress>? addresses)
            {
                if (grainId == UnavailableClient)
                {
                    Interlocked.Increment(ref _failures);
                    throw new ClientNotAvailableException(grainId);
                }

                return inner.TryLocalLookup(grainId, out addresses);
            }

            public ValueTask<List<GrainAddress>> Lookup(GrainId grainId) => inner.Lookup(grainId);
        }

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

        private sealed class RecordingConnection(ConnectionCommon shared)
            : Connection(new RecordingMessageTransport(), shared)
        {
            public List<Message> Messages { get; } = [];

            protected override ConnectionDirection ConnectionDirection => ConnectionDirection.SiloToSilo;

            protected override TimeSpan CloseConnectionTimeout => TimeSpan.FromSeconds(1);

            protected override IMessageCenter MessageCenter => null!;

            public override void Send(Message message) => Messages.Add(message);

            protected override bool PrepareMessageForSend(Message msg) => throw new NotSupportedException();

            protected override void RetryMessage(Message msg, Exception? ex = null) => throw new NotSupportedException();

            protected internal override void RecordMessageReceive(Message msg, int numTotalBytes, int headerBytes) =>
                throw new NotSupportedException();

            protected internal override void RecordMessageSend(Message msg, int numTotalBytes, int headerBytes) =>
                throw new NotSupportedException();

            protected internal override void OnReceivedMessage(Message message) => throw new NotSupportedException();
        }

        private sealed class RecordingMessageTransport : MessageTransport
        {
            public override CancellationToken Closed => default;

            public override IFeatureCollection Features { get; } = new FeatureCollection();

            public override bool EnqueueRead(ReadRequest request) => false;

            public override bool EnqueueWrite(WriteRequest request) => false;

            public override ValueTask CloseAsync(Exception? closeException, CancellationToken cancellationToken = default) => default;
        }

        private sealed class ThrowingGatewayEventObserver : IObserver<GatewayEvents.GatewayEvent>
        {
            public void OnCompleted()
            {
            }

            public void OnError(Exception error)
            {
            }

            public void OnNext(GatewayEvents.GatewayEvent value)
            {
                if (value is GatewayEvents.RequestTrackingStopped or GatewayEvents.DeadSiloRequestRejected)
                {
                    throw new InvalidOperationException("Test diagnostic observer failure.");
                }
            }
        }
    }
}
