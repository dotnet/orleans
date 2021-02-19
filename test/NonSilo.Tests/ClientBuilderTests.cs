using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using TestGrainInterfaces;
using UnitTests.DtosRefOrleans;
using Xunit;

namespace NonSilo.Tests
{
    public class NoOpGatewaylistProvider : IGatewayListProvider
    {
        public TimeSpan MaxStaleness => throw new NotImplementedException();

        public bool IsUpdatable => throw new NotImplementedException();

        public Task<IList<Uri>> GetGateways()
        {
            throw new NotImplementedException();
        }

        public Task InitializeGatewayListProvider()
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Tests for <see cref="ClientBuilder"/>.
    /// </summary>
    [TestCategory("BVT")]
    [TestCategory("ClientBuilder")]
    public class ClientBuilderTests
    {
        /// <summary>
        /// Tests that a client cannot be created without specifying a ClusterId and a ServiceId.
        /// </summary>
        [Fact]
        public void ClientBuilder_ClusterOptionsTest()
        {
            Assert.Throws<OrleansConfigurationException>(() => new ClientBuilder()
                .ConfigureServices(services => services.AddSingleton<IGatewayListProvider, NoOpGatewaylistProvider>())
                .Build());

            Assert.Throws<OrleansConfigurationException>(() => new ClientBuilder()
               .Configure<ClusterOptions>(options => options.ClusterId = "someClusterId")
               .ConfigureServices(services => services.AddSingleton<IGatewayListProvider, NoOpGatewaylistProvider>())
               .Build());

            Assert.Throws<OrleansConfigurationException>(() => new ClientBuilder()
               .Configure<ClusterOptions>(options => options.ServiceId = "someServiceId")
               .ConfigureServices(services => services.AddSingleton<IGatewayListProvider, NoOpGatewaylistProvider>())
               .Build());

            var builder = new ClientBuilder()
                .Configure<ClusterOptions>(options => { options.ClusterId = "someClusterId"; options.ServiceId = "someServiceId"; })
                .ConfigureServices(services => services.AddSingleton<IGatewayListProvider, NoOpGatewaylistProvider>());
            using (var client = builder.Build())
            {
                Assert.NotNull(client);
            }
        }

        /// <summary>
        /// Tests that a client can be created without specifying configuration.
        /// </summary>
        [Fact]
        public void ClientBuilder_NoSpecifiedConfigurationTest()
        {
            var builder = new ClientBuilder()
                .ConfigureDefaults()
                .ConfigureServices(RemoveConfigValidators)
                .ConfigureServices(services => services.AddSingleton<IGatewayListProvider, NoOpGatewaylistProvider>());
            using (var client = builder.Build())
            {
                Assert.NotNull(client);
            }
        }

        [Fact]
        public void ClientBuilder_ThrowsDuringStartupIfNoGrainInterfacesAdded()
        {
            // Add only an assembly with generated serializers but no grain interfaces
            var clientBuilder = new ClientBuilder()
                .UseLocalhostClustering()
                .ConfigureApplicationParts(parts =>
                {
                    parts.ClearApplicationParts();
                })
                .ConfigureServices(services => services.AddSingleton<IGatewayListProvider, NoOpGatewaylistProvider>());

            Assert.Throws<OrleansConfigurationException>(() => clientBuilder.Build());
        }

        /// <summary>
        /// Tests that a builder can not be used to build more than one client.
        /// </summary>
        [Fact]
        public void ClientBuilder_DoubleBuildTest()
        {
            var builder = new ClientBuilder()
                .ConfigureDefaults()
                .ConfigureServices(RemoveConfigValidators)
                .ConfigureServices(services => services.AddSingleton<IGatewayListProvider, NoOpGatewaylistProvider>());
            using (builder.Build())
            {
                Assert.Throws<InvalidOperationException>(() => builder.Build());
            }
        }

        /// <summary>
        /// Tests that the <see cref="IClientBuilder.ConfigureServices"/> delegate works as expected.
        /// </summary>
        [Fact]
        public void ClientBuilder_ServiceProviderTest()
        {
            var builder = new ClientBuilder()
                .ConfigureDefaults()
                .ConfigureServices(RemoveConfigValidators)
                .ConfigureServices(services => services.AddSingleton<IGatewayListProvider, NoOpGatewaylistProvider>());

            Assert.Throws<ArgumentNullException>(() => builder.ConfigureServices(null));

            var registeredFirst = new int[1];
            
            var one = new MyService { Id = 1 };
            builder.ConfigureServices(
                services =>
                {
                    Interlocked.CompareExchange(ref registeredFirst[0], 1, 0);
                    services.AddSingleton(one);
                });

            var two = new MyService { Id = 2 };
            builder.ConfigureServices(
                services =>
                {
                    Interlocked.CompareExchange(ref registeredFirst[0], 2, 0);
                    services.AddSingleton(two);
                });

            using (var client = builder.Build())
            {
                var services = client.ServiceProvider.GetServices<MyService>()?.ToList();
                Assert.NotNull(services);
                
                // Both services should be registered.
                Assert.Equal(2, services.Count);
                Assert.NotNull(services.FirstOrDefault(svc => svc.Id == 1));
                Assert.NotNull(services.FirstOrDefault(svc => svc.Id == 2));

                // Service 1 should have been registered first - the pipeline order should be preserved.
                Assert.Equal(1, registeredFirst[0]);

                // The last registered service should be provided by default.
                Assert.Equal(2, client.ServiceProvider.GetRequiredService<MyService>().Id);
            }
        }

        private static void RemoveConfigValidators(IServiceCollection services)
        {
            var validators = services.Where(descriptor => descriptor.ServiceType == typeof(IConfigurationValidator)).ToList();
            foreach (var validator in validators) services.Remove(validator);
        }

        private class MyService
        {
            public int Id { get; set; }
        }
    }
}
