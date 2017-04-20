using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Description;
using System.Net;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Orleans.ServiceFabric;
using Newtonsoft.Json;
using NSubstitute;
using Orleans.Runtime.Configuration;
using Xunit;

namespace TestServiceFabric
{
    using Microsoft.Orleans.ServiceFabric.Models;
    using Microsoft.Orleans.ServiceFabric.Utilities;

    [TestCategory("ServiceFabric")]
    public class OrleansCommunicationListenerTests
    {
        private readonly ICodePackageActivationContext activationContext = Substitute.For<ICodePackageActivationContext>();

        private readonly NodeContext nodeContext = new NodeContext(
            "bobble",
            new NodeId(BigInteger.One, BigInteger.One),
            BigInteger.One,
            "amazing",
            Dns.GetHostName());

        private readonly MockServiceContext serviceContext;

        private readonly ClusterConfiguration clusterConfig = new ClusterConfiguration { Defaults = { Generation = 864 } };

        public OrleansCommunicationListenerTests()
        {
            serviceContext = new MockServiceContext(
                this.nodeContext,
                this.activationContext,
                "ChocolateMunchingService",
                new Uri("fabric:/Cocoa/ChocolateMunchingService"),
                new byte[0],
                Guid.NewGuid(),
                9823);
        }

        [Fact]
        public async Task SimpleUsageScenarioTest()
        {
            var endpoints = new EndpointsCollection
            {
                CreateEndpoint(OrleansCommunicationListener.SiloEndpointName, 9082),
                CreateEndpoint(OrleansCommunicationListener.GatewayEndpointName, 8888)
            };

            activationContext.GetEndpoints().Returns(_ => endpoints);
            var siloHost = Substitute.For<ISiloHost>();
            var listener = new OrleansCommunicationListener(this.serviceContext, clusterConfig)
            {
                SiloHost = siloHost
            };

            siloHost.NodeConfig.Returns(_ => clusterConfig.CreateNodeConfigurationForSilo(listener.SiloName));

            var result = await listener.OpenAsync(CancellationToken.None);
            var publishedEndpoints = JsonConvert.DeserializeObject<FabricSiloInfo>(result);

            var siloAddress = publishedEndpoints.SiloAddress;
            siloAddress.Generation.ShouldBeEquivalentTo(864);
            siloAddress.Endpoint.Port.ShouldBeEquivalentTo(9082);

            var gatewayAddress = publishedEndpoints.GatewayAddress;
            gatewayAddress.Generation.ShouldBeEquivalentTo(864);
            gatewayAddress.Endpoint.Port.ShouldBeEquivalentTo(8888);

            siloHost.ReceivedWithAnyArgs(1).Start(null, null);
            siloHost.DidNotReceive().Stop();

            siloHost.ClearReceivedCalls();
            await listener.CloseAsync(CancellationToken.None);
            siloHost.Received(1).Stop();
            siloHost.DidNotReceiveWithAnyArgs().Start(null, null);
        }

        [Fact]
        public void MissingEndpointsCauseException()
        {
            var endpoints = new EndpointsCollection();
            activationContext.GetEndpoints().Returns(_ => endpoints);

            // Check for the silo endpoint.
            var exception = Assert.Throws<KeyNotFoundException>(() => new OrleansCommunicationListener(serviceContext, clusterConfig));
            Assert.Contains(OrleansCommunicationListener.SiloEndpointName, exception.Message);

            // Check for the proxy endpoint.
            endpoints.Add(CreateEndpoint(OrleansCommunicationListener.SiloEndpointName, 9082));
            exception = Assert.Throws<KeyNotFoundException>(() => new OrleansCommunicationListener(serviceContext, clusterConfig));
            Assert.Contains(OrleansCommunicationListener.GatewayEndpointName, exception.Message);
        }

        [Fact]
        public void AbortStopAndDisposesSilo()
        {
            var endpoints = new EndpointsCollection
            {
                CreateEndpoint(OrleansCommunicationListener.SiloEndpointName, 9082),
                CreateEndpoint(OrleansCommunicationListener.GatewayEndpointName, 8888)
            };

            activationContext.GetEndpoints().Returns(_ => endpoints);
            var siloHost = Substitute.For<ISiloHost>();
            var listener = new OrleansCommunicationListener(
                serviceContext,
                new ClusterConfiguration())
            {
                SiloHost = siloHost
            };

            listener.Abort();
            siloHost.ReceivedWithAnyArgs(1).Stop();
            siloHost.ReceivedWithAnyArgs(1).Dispose();
            siloHost.DidNotReceiveWithAnyArgs().Start(null, null);
        }

        [Fact]
        public async Task CloseStopsSilo()
        {
            var endpoints = new EndpointsCollection
            {
                CreateEndpoint(OrleansCommunicationListener.SiloEndpointName, 9082),
                CreateEndpoint(OrleansCommunicationListener.GatewayEndpointName, 8888)
            };

            activationContext.GetEndpoints().Returns(_ => endpoints);
            var siloHost = Substitute.For<ISiloHost>();
            var listener = new OrleansCommunicationListener(
                serviceContext,
                new ClusterConfiguration())
            {
                SiloHost = siloHost
            };

            await listener.CloseAsync(CancellationToken.None);
            siloHost.ReceivedWithAnyArgs(1).Stop();
            siloHost.DidNotReceiveWithAnyArgs().Dispose();
            siloHost.DidNotReceiveWithAnyArgs().Start(null, null);
        }

        private static EndpointResourceDescription CreateEndpoint(string name, int port)
        {
            var endpoint = new EndpointResourceDescription { Name = name };
            typeof(EndpointResourceDescription).GetProperty("Port")
                .GetSetMethod(true)
                .Invoke(endpoint, new object[] {port});

            return endpoint;
        }
    }
}
