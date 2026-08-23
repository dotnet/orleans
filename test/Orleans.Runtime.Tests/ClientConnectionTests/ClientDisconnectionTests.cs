#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainReferences;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.ClientConnectionTests;

[TestCategory("BVT"), TestCategory("MultiClient"), TestCategory("Lifecycle")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class ClientDisconnectionTests(ClientDisconnectionTests.Fixture fixture) : IClassFixture<ClientDisconnectionTests.Fixture>
{
    private readonly InProcessTestCluster _cluster = fixture.Cluster;

    public sealed class Fixture : IAsyncLifetime
    {
        private InProcessTestCluster? _cluster;
        public InProcessTestCluster Cluster => _cluster!;

        public async ValueTask InitializeAsync()
        {
            var builder = new InProcessTestClusterBuilder(2);
            _cluster = builder.Build();
            await _cluster.DeployAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_cluster != null)
            {
                await _cluster.DisposeAsync();
            }
        }

    }

    [Fact]
    public async Task ResponseAcrossMultipleGateways_ClearsOwningGatewayBeforeClientDrop()
    {
        var clientA = await _cluster.GetClientAsync("OwnerClientA");
        var clientB = await _cluster.GetClientAsync("OwnerClientB");
        var observerB = new EchoGrainObserver();
        var observerBReference = clientB.CreateObjectReference<IEchoGrainObserver>(observerB);
        observerB.SelfReference = observerBReference;
        var observerBId = observerBReference.GetGrainId();
        var aToB = (IEchoGrainObserver)clientA.ServiceProvider.GetRequiredService<GrainReferenceActivator>().CreateReference(
            observerBId,
            GrainInterfaceType.Create("IEchoGrainObserver"));
        var responseTask = aToB.EchoAsync("owner-routed response");

        await observerB.WaitForCallAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(ClientGrainId.TryParse(observerBId, out var clientBId));
        var gateways = _cluster.Silos
            .Select(static silo => silo.ServiceProvider.GetRequiredService<MessageCenter>().Gateway!)
            .ToArray();
        await WaitUntilAsync(
            () => gateways.Sum(gateway => gateway.GetOutstandingRequestCount(clientBId)) == 1,
            TimeSpan.FromSeconds(10));

        var ownerIndex = Array.FindIndex(gateways, gateway => gateway.GetOutstandingRequestCount(clientBId) == 1);
        var ownerDetails = _cluster.Silos[ownerIndex].ServiceProvider.GetRequiredService<ILocalSiloDetails>();
        var requestKey = gateways[ownerIndex].GetOutstandingRequestKeys(clientBId).Single();
        var routingRequest = new Message
        {
            SendingSilo = ownerDetails.SiloAddress,
        };
        routingRequest.SetGatewayRequestOwner(ownerDetails.GatewayAddress, ownerDetails.SiloAddress);
        var responseThroughOtherGateway = new Message
        {
            Direction = Message.Directions.Response,
            Id = requestKey.CorrelationId,
            SendingGrain = observerBId,
            TargetGrain = requestKey.GrainId,
        };
        responseThroughOtherGateway.ApplyGatewayRequestOwner(routingRequest);
        await gateways[1 - ownerIndex].RecordClientResponse(responseThroughOtherGateway);
        Assert.Equal(ownerDetails.SiloAddress, gateways[1 - ownerIndex].TryToReroute(responseThroughOtherGateway));
        Assert.All(gateways, gateway => Assert.Equal(0, gateway.GetOutstandingRequestCount(clientBId)));

        observerB.UnblockResponse();
        Assert.Equal("owner-routed response", await responseTask);
        await WaitUntilAsync(
            () => gateways.All(gateway => gateway.GetOutstandingRequestCount(clientBId) == 0),
            TimeSpan.FromSeconds(10));

        await _cluster.RemoveClientAsync("OwnerClientB");
        await clientA.GetGrain<IManagementGrain>(0).DropDisconnectedClients(excludeRecent: false);
        Assert.All(gateways, gateway => Assert.Equal(0, gateway.GetOutstandingRequestCount(clientBId)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientReceivesRejectionWhenTargetClientDisconnected(bool hostedClient)
    {
        var clientA = hostedClient ? _cluster.Silos[0].ServiceProvider.GetRequiredService<IClusterClient>() : await _cluster.GetClientAsync("ClientA");
        var clientB = await _cluster.GetClientAsync("ClientB");

        var observerA = new EchoGrainObserver();
        observerA.SelfReference = clientA.CreateObjectReference<IEchoGrainObserver>(observerA);

        var observerB = new EchoGrainObserver();
        observerB.SelfReference = clientB.CreateObjectReference<IEchoGrainObserver>(observerB);

        // Exchange references, so each one has a reference to the other which is bound to its client.
        var observerBId = observerB.SelfReference.GetGrainId();
        var aToB = (IEchoGrainObserver)clientA.ServiceProvider.GetRequiredService<GrainReferenceActivator>().CreateReference(observerBId, GrainInterfaceType.Create("IEchoGrainObserver"));

        observerB.UnblockResponse();
        await aToB.EchoAsync("Hi from A.");

        const string message = "Hello from Client A";
        observerB.UnblockResponse();
        var response = await aToB.EchoAsync(message);
        Assert.Equal(message, response);

        await _cluster.RemoveClientAsync("ClientB");

        observerB.UnblockResponse();
        var responseTask = aToB.EchoAsync(message);
        await Assert.ThrowsAsync<TimeoutException>(async () => await responseTask.WaitAsync(TimeSpan.FromMilliseconds(200)));
        Assert.False(responseTask.IsCompleted, "The task should not complete before the client has been dropped.");

        // Use IManagementGrain to force all Gateways to drop defunct clients.
        var managementGrain = clientA.GetGrain<IManagementGrain>(0);
        await managementGrain.DropDisconnectedClients(excludeRecent: false);

        // The call should promptly fail with a ClientNotAvailableException.
        await Assert.ThrowsAsync<ClientNotAvailableException>(() => responseTask);

        // Attempt call from A to B after B disconnected, expect rejection
        await Assert.ThrowsAsync<ClientNotAvailableException>(async () =>
        {
            // This call should fail because Client B is gone and the gateway should reject it.
            await aToB.EchoAsync("Calling disconnected client");
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientReceivesRejectionForResponseWhenTargetClientDisconnected(bool hostedClient)
    {
        var clientA = hostedClient ? _cluster.Silos[0].ServiceProvider.GetRequiredService<IClusterClient>() : await _cluster.GetClientAsync("ClientA");
        var clientB = await _cluster.GetClientAsync("ClientB");

        var observerA = new EchoGrainObserver();
        observerA.SelfReference = clientA.CreateObjectReference<IEchoGrainObserver>(observerA);

        var observerB = new EchoGrainObserver();
        observerB.SelfReference = clientB.CreateObjectReference<IEchoGrainObserver>(observerB);

        // Create references from each to the other.
        var aToB = (IEchoGrainObserver)clientA.ServiceProvider.GetRequiredService<GrainReferenceActivator>().CreateReference(observerB.SelfReference.GetGrainId(), GrainInterfaceType.Create("IEchoGrainObserver"));
        var bToA = (IEchoGrainObserver)clientB.ServiceProvider.GetRequiredService<GrainReferenceActivator>().CreateReference(observerA.SelfReference.GetGrainId(), GrainInterfaceType.Create("IEchoGrainObserver"));

        // B -> A (blocked)
        var responseTask = bToA.EchoAsync("Hi from B.");

        // B disconnects
        await _cluster.RemoveClientAsync("ClientB");

        // B's pending request should be promptly rejected locally.
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => responseTask);
        Assert.True(
            exception is OperationCanceledException or OrleansMessageRejectionException or SiloUnavailableException,
            $"Unexpected exception type: {exception.GetType()}");

        // A sends response to B.
        observerA.UnblockResponse();

        // Purge disconnected clients (rejecting pending response)
        var managementGrain = clientA.GetGrain<IManagementGrain>(0);
        await managementGrain.DropDisconnectedClients(excludeRecent: false);
    }

    [Fact]
    public async Task ClientCannotSendMessageAfterDisconnecting()
    {
        var clientA = await _cluster.GetClientAsync("ClientA");
        var observerA = new EchoGrainObserver();
        observerA.SelfReference = clientA.CreateObjectReference<IEchoGrainObserver>(observerA);

        await _cluster.RemoveClientAsync("ClientA");

        // Attempt to send a message after disconnect
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await observerA.SelfReference.EchoAsync("Should fail");
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MessageToDisconnectingClientIsRejected(bool hostedClient)
    {
        var clientA = hostedClient ? _cluster.Silos[0].ServiceProvider.GetRequiredService<IClusterClient>() : await _cluster.GetClientAsync("ClientA");
        var clientB = await _cluster.GetClientAsync("ClientB");

        var observerA = new EchoGrainObserver();
        observerA.SelfReference = clientA.CreateObjectReference<IEchoGrainObserver>(observerA);
        var observerB = new EchoGrainObserver();
        observerB.SelfReference = clientB.CreateObjectReference<IEchoGrainObserver>(observerB);

        // Create references from each to the other.
        var aToB = (IEchoGrainObserver)clientA.ServiceProvider.GetRequiredService<GrainReferenceActivator>().CreateReference(observerB.SelfReference.GetGrainId(), GrainInterfaceType.Create("IEchoGrainObserver"));
        var bToA = (IEchoGrainObserver)clientB.ServiceProvider.GetRequiredService<GrainReferenceActivator>().CreateReference(observerA.SelfReference.GetGrainId(), GrainInterfaceType.Create("IEchoGrainObserver"));

        // Start a call but disconnect B before it can respond
        var responseTask = aToB.EchoAsync("Test message");
        await _cluster.RemoveClientAsync("ClientB");

        // Purge disconnected clients (rejecting pending response)
        var managementGrain = clientA.GetGrain<IManagementGrain>(0);
        await managementGrain.DropDisconnectedClients(excludeRecent: false);

        // The call should be rejected
        await Assert.ThrowsAsync<ClientNotAvailableException>(async () => await responseTask);
    }

    [GrainInterfaceType("IEchoGrainObserver")]
    public interface IEchoGrainObserver : IGrainObserver
    {
        Task<string> EchoAsync(string message);
        Task SendSelfReferenceToPeerAsync(IEchoGrainObserver peer);
        Task SetPeerReferenceAsync(IEchoGrainObserver other);
    }

    public sealed class EchoGrainObserver : IEchoGrainObserver
    {
        private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _tcs = new();
        public IEchoGrainObserver? SelfReference { get; set; }
        public IEchoGrainObserver? PeerReference { get; private set; }
        public void UnblockResponse()
        {
            _tcs.SetResult();
        }

        public Task WaitForCallAsync() => _entered.Task;

        public async Task<string> EchoAsync(string message)
        {
            _entered.TrySetResult();
            await _tcs.Task;
            _tcs = new();
            _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return message;
        }

        public Task SetPeerReferenceAsync(IEchoGrainObserver other)
        {
            PeerReference = other;
            return Task.CompletedTask;
        }

        public async Task SendSelfReferenceToPeerAsync(IEchoGrainObserver peer)
        {
            await peer.SetPeerReferenceAsync(SelfReference!);
        }

    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected gateway request-tracking state was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
