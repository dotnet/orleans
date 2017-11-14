using System;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using TestGrainInterfaces;
using Xunit;

namespace NonSilo.Tests
{
    /// <summary>
    /// Tests for <see cref="SiloHostBuilder"/>.
    /// </summary>
    [TestCategory("BVT")]
    [TestCategory("SiloHostBuilder")]
    public class SiloHostBuilderTests
    {
        /// <summary>
        /// Tests that the silo builder will fail if no assemblies are configured.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_AssembliesTest()
        {
            var builder = (ISiloHostBuilder) new SiloHostBuilder();
            Assert.Throws<OrleansConfigurationException>(() => builder.Build());

            // Adding an application assembly causes the 
            builder = new SiloHostBuilder().UseConfiguration(new ClusterConfiguration()).ConfigureApplicationPartManager(parts => parts.AddApplicationPart(typeof(IAccountGrain).Assembly));
            using (var silo = builder.Build())
            {
                Assert.NotNull(silo);
            }
        }

        /// <summary>
        /// Tests that a silo can be created without specifying configuration.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_NoSpecifiedConfigurationTest()
        {
            var builder = SiloHostBuilder.CreateDefault()
                                         .ConfigureApplicationPartManager(parts => parts.AddFromApplicationBaseDirectory().AddFromAppDomain())
                                         .UseConfiguration(new ClusterConfiguration()).ConfigureServices(RemoveConfigValidators);
            using (var silo = builder.Build())
            {
                Assert.NotNull(silo);
            }
        }

        /// <summary>
        /// Tests that a builder can not be used to build more than one silo.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_DoubleBuildTest()
        {
            var builder = SiloHostBuilder.CreateDefault()
                                         .ConfigureApplicationPartManager(parts => parts.AddFromApplicationBaseDirectory().AddFromAppDomain())
                                         .UseConfiguration(new ClusterConfiguration()).ConfigureServices(RemoveConfigValidators);
            using (builder.Build())
            {
                Assert.Throws<InvalidOperationException>(() => builder.Build());
            }
        }

        /// <summary>
        /// Tests that configuration cannot be specified twice.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_DoubleSpecifyConfigurationTest()
        {
            var builder = SiloHostBuilder.CreateDefault()
                                         .ConfigureApplicationPartManager(parts => parts.AddFromApplicationBaseDirectory().AddFromAppDomain())
                                         .ConfigureServices(RemoveConfigValidators).UseConfiguration(new ClusterConfiguration()).UseConfiguration(new ClusterConfiguration());
            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        /// <summary>
        /// Tests that a silo can be created without specifying configuration.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_NullConfigurationTest()
        {
            var builder = SiloHostBuilder.CreateDefault()
                                         .ConfigureApplicationPartManager(parts => parts.AddFromApplicationBaseDirectory().AddFromAppDomain())
                                         .ConfigureServices(RemoveConfigValidators);
            Assert.Throws<ArgumentNullException>(() => builder.UseConfiguration(null));
        }

        /// <summary>
        /// Tests that the <see cref="ISiloHostBuilder.ConfigureServices"/> delegate works as expected.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_ServiceProviderTest()
        {
            var builder = SiloHostBuilder.CreateDefault()
                                         .ConfigureApplicationPartManager(parts => parts.AddFromApplicationBaseDirectory().AddFromAppDomain())
                                         .UseConfiguration(new ClusterConfiguration()).ConfigureServices(RemoveConfigValidators);

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

            using (var silo = builder.Build())
            {
                var services = silo.Services.GetServices<MyService>()?.ToList();
                Assert.NotNull(services);

                // Both services should be registered.
                Assert.Equal(2, services.Count);
                Assert.NotNull(services.FirstOrDefault(svc => svc.Id == 1));
                Assert.NotNull(services.FirstOrDefault(svc => svc.Id == 2));

                // Service 1 should have been registered first - the pipeline order should be preserved.
                Assert.Equal(1, registeredFirst[0]);

                // The last registered service should be provided by default.
                Assert.Equal(2, silo.Services.GetRequiredService<MyService>().Id);
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