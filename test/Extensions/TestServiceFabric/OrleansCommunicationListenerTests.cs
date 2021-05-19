using System;
using System.Fabric;
using System.Fabric.Description;
using System.Net;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Hosting.ServiceFabric;
using Orleans.ServiceFabric;
using Xunit;

namespace TestServiceFabric
{
    [TestCategory("ServiceFabric"), TestCategory("Functional")]
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
                CreateEndpoint(ServiceFabricConstants.SiloEndpointName, 9082),
                CreateEndpoint(ServiceFabricConstants.GatewayEndpointName, 8888)
            };

            activationContext.GetEndpoints().Returns(_ => endpoints);

            var listener = new OrleansCommunicationListener(
                builder =>
                {
                    builder.ConfigureServices(
                        services =>
                        {
                            // Use our mock silo host.
                            services.Replace(ServiceDescriptor.Singleton<ISiloHost>(sp => Substitute.ForPartsOf<MockSiloHost>(sp)));
                        });

                    builder.UseLocalhostClustering();
                    builder.Configure<EndpointOptions>(options =>
                    {
                        options.SiloPort = 9082;
                        options.GatewayPort = 8888;
                    });
                });

            var result = await listener.OpenAsync(CancellationToken.None);

            var siloHost = listener.Host;
            var publishedEndpoints = JsonConvert.DeserializeObject<FabricSiloInfo>(result);

            var siloAddress = publishedEndpoints.SiloAddress;
            siloAddress.Generation.Should().NotBe(0);
            siloAddress.Endpoint.Port.ShouldBeEquivalentTo(9082);

            var gatewayAddress = publishedEndpoints.GatewayAddress;
            gatewayAddress.Generation.Should().Be(0);
            gatewayAddress.Endpoint.Port.ShouldBeEquivalentTo(8888);

            await siloHost.ReceivedWithAnyArgs(1).StartAsync(Arg.Is<CancellationToken>(c => !c.IsCancellationRequested));
            await siloHost.DidNotReceive().StopAsync(Arg.Any<CancellationToken>());

            siloHost.ClearReceivedCalls();
            await listener.CloseAsync(CancellationToken.None);
            await siloHost.ReceivedWithAnyArgs(1).StopAsync(Arg.Is<CancellationToken>(c => !c.IsCancellationRequested));
            await siloHost.DidNotReceiveWithAnyArgs().StartAsync(Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task AbortStopAndDisposesSilo()
        {
            var endpoints = new EndpointsCollection
            {
                CreateEndpoint(ServiceFabricConstants.SiloEndpointName, 9082),
                CreateEndpoint(ServiceFabricConstants.GatewayEndpointName, 8888)
            };

            activationContext.GetEndpoints().Returns(_ => endpoints);
            var listener = new OrleansCommunicationListener(
                builder =>
                {
                    builder.ConfigureServices(
                        services =>
                        {
                            // Use our mock silo host.
                            services.Replace(ServiceDescriptor.Singleton<ISiloHost>(sp => Substitute.ForPartsOf<MockSiloHost>(sp)));
                        });

                    builder.Configure<EndpointOptions>(options =>
                    {
                        options.SiloPort = 9082;
                        options.GatewayPort = 8888;
                    });
                    builder.UseLocalhostClustering();
                });

            await listener.OpenAsync(CancellationToken.None);
            var siloHost = listener.Host;
            siloHost.ClearReceivedCalls();

            listener.Abort();
            await siloHost.ReceivedWithAnyArgs(1).StopAsync(Arg.Is<CancellationToken>(c => c.IsCancellationRequested));
            await siloHost.DidNotReceiveWithAnyArgs().StartAsync(Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task CloseStopsSilo()
        {
            var endpoints = new EndpointsCollection
            {
                CreateEndpoint(ServiceFabricConstants.SiloEndpointName, 9082),
                CreateEndpoint(ServiceFabricConstants.GatewayEndpointName, 8888)
            };

            activationContext.GetEndpoints().Returns(_ => endpoints);
            var listener = new OrleansCommunicationListener(
                builder =>
                {
                    builder.ConfigureServices(
                        services =>
                        {
                            // Use our mock silo host.
                            services.Replace(ServiceDescriptor.Singleton<ISiloHost>(sp => Substitute.ForPartsOf<MockSiloHost>(sp)));
                        });

                    builder.Configure<EndpointOptions>(options =>
                    {
                        options.SiloPort = 9082;
                        options.GatewayPort = 8888;
                    });
                    builder.UseLocalhostClustering();
                });

            await listener.OpenAsync(CancellationToken.None);
            var siloHost = listener.Host;
            siloHost.ClearReceivedCalls();
            await listener.CloseAsync(CancellationToken.None);
            await siloHost.ReceivedWithAnyArgs(1).StopAsync(Arg.Is<CancellationToken>(c => !c.IsCancellationRequested));
            await siloHost.DidNotReceiveWithAnyArgs().StartAsync(Arg.Any<CancellationToken>());
        }

        private static EndpointResourceDescription CreateEndpoint(string name, int port)
        {
            var endpoint = new EndpointResourceDescription { Name = name };
            typeof(EndpointResourceDescription).GetProperty("Port")
                .GetSetMethod(true)
                .Invoke(endpoint, new object[] { port });

            return endpoint;
        }

        public class MockSiloHost : ISiloHost
        {
            private readonly TaskCompletionSource<int> stopped = new TaskCompletionSource<int>();

            public MockSiloHost(IServiceProvider services)
            {
                this.Services = services;
            }

            /// <inheritdoc />
            public virtual IServiceProvider Services { get; }

            /// <inheritdoc />
            public virtual Task Stopped => this.stopped.Task;

            /// <inheritdoc />
            public virtual async Task StartAsync(CancellationToken cancellationToken)
            {
                // Await to avoid compiler warnings.
                await Task.CompletedTask;
            }

            /// <inheritdoc />
            public virtual Task StopAsync(CancellationToken cancellationToken)
            {
                this.stopped.TrySetResult(0);
                return Task.CompletedTask;
            }

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => default;
        }
    }

    /// <summary>
    /// A grain which is not used but which satisfies startup configuration checks.
    /// </summary>
    public class UnusedGrain : Grain { }
    public interface IUnusedGrain : IGrain { }
}
