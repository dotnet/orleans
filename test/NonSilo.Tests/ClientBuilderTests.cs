using System;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using TestGrainInterfaces;
using Xunit;

namespace NonSilo.Tests
{
    /// <summary>
    /// Tests for <see cref="ClientBuilder"/>.
    /// </summary>
    [TestCategory("BVT")]
    [TestCategory("ClientBuilder")]
    public class ClientBuilderTests
    {
        /// <summary>
        /// Tests that the client builder will fail if no assemblies are configured.
        /// </summary>
        [Fact]
        public void ClientBuilder_AssembliesTest()
        {
            var builder = (IClientBuilder)new ClientBuilder();
            Assert.Throws<OrleansConfigurationException>(() => builder.Build());

            // Adding an application assembly allows the builder to build successfully.
            builder = new ClientBuilder().ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IAccountGrain).Assembly));
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
            var builder = ClientBuilder.CreateDefault().ConfigureServices(RemoveConfigValidators);
            using (var client = builder.Build())
            {
                Assert.NotNull(client);
            }
        }

        /// <summary>
        /// Tests that a builder can not be used to build more than one client.
        /// </summary>
        [Fact]
        public void ClientBuilder_DoubleBuildTest()
        {
            var builder = ClientBuilder.CreateDefault().ConfigureServices(RemoveConfigValidators);
            using (builder.Build())
            {
                Assert.Throws<InvalidOperationException>(() => builder.Build());
            }
        }

        /// <summary>
        /// Tests that configuration cannot be specified twice.
        /// </summary>
        [Fact]
        public void ClientBuilder_DoubleSpecifyConfigurationTest()
        {
            var builder = ClientBuilder.CreateDefault().ConfigureServices(RemoveConfigValidators)
                .UseConfiguration(new ClientConfiguration())
                .UseConfiguration(new ClientConfiguration());
            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        /// <summary>
        /// Tests that a client can be created without specifying configuration.
        /// </summary>
        [Fact]
        public void ClientBuilder_NullConfigurationTest()
        {
            var builder = ClientBuilder.CreateDefault().ConfigureServices(RemoveConfigValidators);
            Assert.Throws<ArgumentNullException>(() => builder.UseConfiguration(null));
        }
        
        /// <summary>
        /// Tests that the <see cref="IClientBuilder.ConfigureServices"/> delegate works as expected.
        /// </summary>
        [Fact]
        public void ClientBuilder_ServiceProviderTest()
        {
            var builder = ClientBuilder.CreateDefault().ConfigureServices(RemoveConfigValidators);

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
