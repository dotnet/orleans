#nullable enable
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.GrainReferences;
using Orleans.Runtime;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils; // For TestClusterPortAllocator
using TestGrainInterfaces; // Assuming this is accessible from Tester project
using UnitTests.GrainInterfaces;
using Xunit;

namespace Tester.ClientConnectionTests;

[TestCategory("BVT"), TestCategory("MultiClient"), TestCategory("Lifecycle")]
public class ClientDisconnectionTests(ClientDisconnectionTests.Fixture fixture) : IClassFixture<ClientDisconnectionTests.Fixture>
{
    private readonly InProcessTestCluster _cluster = fixture.Cluster;

    public sealed class Fixture : IAsyncLifetime
    {
        private InProcessTestCluster? _cluster;
        public InProcessTestCluster Cluster => _cluster!;

        public async Task InitializeAsync()
        {
            var builder = new InProcessTestClusterBuilder();
            _cluster = builder.Build();
            await _cluster.DeployAsync();
        }

        public async Task DisposeAsync()
        {
            if (_cluster != null)
            {
                await _cluster.DisposeAsync();
            }
        }
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

        // Which particular exception occurs is not deterministic
        if (exception is OperationCanceledException)
        {
            Assert.Equal("The host is shutting down.", exception.Message);
        }
        else if (exception is OrleansMessageRejectionException omre)
        {
            Assert.Equal("Client is shutting down.", exception.Message);
        }
        else
        {
            Assert.Fail($"Unexpected exception type: {exception.GetType()}");
        }

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
        private TaskCompletionSource _tcs = new();
        public IEchoGrainObserver? SelfReference { get; set; }
        public IEchoGrainObserver? PeerReference { get; private set; }
        public void UnblockResponse()
        {
            _tcs.SetResult();
        }

        public async Task<string> EchoAsync(string message)
        {
            await _tcs.Task;
            _tcs = new();
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
}
