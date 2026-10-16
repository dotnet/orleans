#nullable enable
using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Xunit;

namespace NonSilo.Tests
{
    /// <summary>
    /// Tests typed values read from the <c>Orleans</c> configuration section by the silo and client builders.
    /// </summary>
    [TestCategory("BVT")]
    [TestCategory("Providers")]
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    public class ConfigurationValueTests
    {
        [Fact]
        public void SiloBuilder_EndpointValues_Bind()
        {
            using var host = BuildSiloHost(
                ("Orleans:Endpoints:SiloPort", "11112"),
                ("Orleans:Endpoints:GatewayPort", "30001"),
                ("Orleans:Endpoints:AdvertisedIPAddress", "10.0.0.1"),
                ("Orleans:Endpoints:SiloListeningEndpoint", "0.0.0.0:11113"),
                ("Orleans:Endpoints:GatewayListeningEndpoint", "[::]:30002"));

            var options = host.Services.GetRequiredService<IOptions<EndpointOptions>>().Value;

            Assert.Equal(11112, options.SiloPort);
            Assert.Equal(30001, options.GatewayPort);
            Assert.Equal(IPAddress.Parse("10.0.0.1"), options.AdvertisedIPAddress);
            Assert.Equal(new IPEndPoint(IPAddress.Any, 11113), options.SiloListeningEndpoint);
            Assert.Equal(new IPEndPoint(IPAddress.IPv6Any, 30002), options.GatewayListeningEndpoint);
        }

        [Theory]
        [InlineData(nameof(EndpointOptions.SiloPort), "not-a-port", typeof(int))]
        [InlineData(nameof(EndpointOptions.GatewayPort), "not-a-port", typeof(int))]
        [InlineData(nameof(EndpointOptions.AdvertisedIPAddress), "not-an-address", typeof(IPAddress))]
        [InlineData(nameof(EndpointOptions.SiloListeningEndpoint), "not-an-endpoint", typeof(IPEndPoint))]
        [InlineData(nameof(EndpointOptions.GatewayListeningEndpoint), "not-an-endpoint", typeof(IPEndPoint))]
        [InlineData(nameof(EndpointOptions.SiloPort), "   ", typeof(int))]
        [InlineData(nameof(EndpointOptions.GatewayPort), "   ", typeof(int))]
        [InlineData(nameof(EndpointOptions.AdvertisedIPAddress), "   ", typeof(IPAddress))]
        [InlineData(nameof(EndpointOptions.SiloListeningEndpoint), "   ", typeof(IPEndPoint))]
        [InlineData(nameof(EndpointOptions.GatewayListeningEndpoint), "   ", typeof(IPEndPoint))]
        public void SiloBuilder_InvalidEndpointValue_Throws(string key, string invalidValue, Type expectedType)
        {
            using var host = BuildSiloHost(($"Orleans:Endpoints:{key}", invalidValue));

            var exception = Assert.Throws<InvalidOperationException>(
                () => host.Services.GetRequiredService<IOptions<EndpointOptions>>().Value);

            Assert.Contains($"at 'Orleans:Endpoints:{key}'", exception.Message);
            Assert.Contains($"type '{expectedType}'", exception.Message);
        }

        [Theory]
        [InlineData("not-a-boolean")]
        [InlineData("   ")]
        public void SiloBuilder_InvalidEnableDistributedTracing_Throws(string invalidValue)
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => BuildSiloHost(("Orleans:EnableDistributedTracing", invalidValue)));

            Assert.Contains("at 'Orleans:EnableDistributedTracing'", exception.Message);
            Assert.Contains($"type '{typeof(bool)}'", exception.Message);
        }

        [Theory]
        [InlineData("not-a-boolean")]
        [InlineData("   ")]
        public void ClientBuilder_InvalidEnableDistributedTracing_Throws(string invalidValue)
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => BuildClientHost(("Orleans:EnableDistributedTracing", invalidValue)));

            Assert.Contains("at 'Orleans:EnableDistributedTracing'", exception.Message);
            Assert.Contains($"type '{typeof(bool)}'", exception.Message);
        }

        [Fact]
        public void SiloBuilder_EmptyEndpointValues_PreserveDefaults()
        {
            using var host = BuildSiloHost(
                ("Orleans:Endpoints:SiloPort", ""),
                ("Orleans:Endpoints:GatewayPort", ""),
                ("Orleans:Endpoints:AdvertisedIPAddress", ""),
                ("Orleans:Endpoints:SiloListeningEndpoint", ""),
                ("Orleans:Endpoints:GatewayListeningEndpoint", ""));

            var options = host.Services.GetRequiredService<IOptions<EndpointOptions>>().Value;

            Assert.Equal(EndpointOptions.DEFAULT_SILO_PORT, options.SiloPort);
            Assert.Equal(EndpointOptions.DEFAULT_GATEWAY_PORT, options.GatewayPort);
            Assert.Null(options.SiloListeningEndpoint);
            Assert.Null(options.GatewayListeningEndpoint);
        }

        [Theory]
        [InlineData("", false)]
        [InlineData("false", false)]
        [InlineData("true", true)]
        public void SiloBuilder_EnableDistributedTracing_ConfiguresActivityPropagation(string value, bool expected)
        {
            using var host = BuildSiloHost(("Orleans:EnableDistributedTracing", value));

            Assert.Equal(expected, host.Services.GetService<DistributedContextPropagator>() is not null);
        }

        [Theory]
        [InlineData("", false)]
        [InlineData("false", false)]
        [InlineData("true", true)]
        public void ClientBuilder_EnableDistributedTracing_ConfiguresActivityPropagation(string value, bool expected)
        {
            using var host = BuildClientHost(("Orleans:EnableDistributedTracing", value));

            Assert.Equal(expected, host.Services.GetService<DistributedContextPropagator>() is not null);
        }

        [Fact]
        public void ClientBuilder_EmptyStaticClusteringGateway_IsSkipped()
        {
            using var host = BuildClientHost(
                ("Orleans:Clustering:ProviderType", "Static"),
                ("Orleans:Clustering:Gateways:0", "10.0.0.1:30000"),
                ("Orleans:Clustering:Gateways:1", ""));

            var options = host.Services.GetRequiredService<IOptions<StaticGatewayListProviderOptions>>().Value;

            Assert.Equal([new IPEndPoint(IPAddress.Parse("10.0.0.1"), 30000).ToGatewayUri()], options.Gateways);
        }

        [Fact]
        public void ClientBuilder_StaticClusteringGateways_Bind()
        {
            using var host = BuildClientHost(
                ("Orleans:Clustering:ProviderType", "Static"),
                ("Orleans:Clustering:Gateways:0", "10.0.0.1:30000"),
                ("Orleans:Clustering:Gateways:1", "10.0.0.2:30001"));

            var options = host.Services.GetRequiredService<IOptions<StaticGatewayListProviderOptions>>().Value;

            Assert.Equal(
                [new IPEndPoint(IPAddress.Parse("10.0.0.1"), 30000).ToGatewayUri(), new IPEndPoint(IPAddress.Parse("10.0.0.2"), 30001).ToGatewayUri()],
                options.Gateways);
        }

        [Theory]
        [InlineData("not-an-endpoint")]
        [InlineData("   ")]
        public void ClientBuilder_InvalidStaticClusteringGateway_Throws(string invalidValue)
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => BuildClientHost(
                    ("Orleans:Clustering:ProviderType", "Static"),
                    ("Orleans:Clustering:Gateways:0", "10.0.0.1:30000"),
                    ("Orleans:Clustering:Gateways:1", invalidValue)));

            Assert.Contains("at 'Orleans:Clustering:Gateways:1'", exception.Message);
            Assert.Contains($"type '{typeof(IPEndPoint)}'", exception.Message);
        }

        [Fact]
        public void SiloBuilder_InvalidMemoryGrainStorageValue_Throws()
        {
            using var host = BuildSiloHost(
                ("Orleans:GrainStorage:MyStorage:ProviderType", "Memory"),
                ("Orleans:GrainStorage:MyStorage:NumStorageGrains", "not-an-integer"));

            var exception = Assert.Throws<InvalidOperationException>(
                () => host.Services.GetRequiredService<IOptionsMonitor<MemoryGrainStorageOptions>>().Get("MyStorage"));

            Assert.Contains("at 'Orleans:GrainStorage:MyStorage:NumStorageGrains'", exception.Message);
            Assert.Contains($"type '{typeof(int)}'", exception.Message);
        }

        private static IHost BuildSiloHost(params (string Key, string Value)[] settings)
            => new HostBuilder()
                .ConfigureAppConfiguration(configBuilder => configBuilder.AddInMemoryCollection(CreateConfiguration(settings)))
                .UseOrleans(_ => { })
                .Build();

        private static IHost BuildClientHost(params (string Key, string Value)[] settings)
            => new HostBuilder()
                .ConfigureAppConfiguration(configBuilder => configBuilder.AddInMemoryCollection(CreateConfiguration(settings)))
                .UseOrleansClient(_ => { })
                .Build();

        private static Dictionary<string, string?> CreateConfiguration((string Key, string Value)[] settings)
        {
            var configuration = new Dictionary<string, string?>
            {
                ["Orleans:ClusterId"] = "test-cluster",
                ["Orleans:ServiceId"] = "test-service",
            };

            foreach (var (key, value) in settings)
            {
                configuration[key] = value;
            }

            return configuration;
        }
    }
}
