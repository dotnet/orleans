using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using TestGrainInterfaces;
using Xunit;

namespace NonSilo.Tests
{
    public class NoOpMembershipTable : IMembershipTable
    {
        public Task DeleteMembershipTableEntries(string clusterId)
        {
            return Task.CompletedTask;
        }

        public Task InitializeMembershipTable(bool tryInitTableVersion)
        {
            return Task.CompletedTask;
        }

        public Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
        {
            return Task.FromResult(true);
        }

        public Task<MembershipTableData> ReadAll()
        {
            throw new NotImplementedException();
        }

        public Task<MembershipTableData> ReadRow(SiloAddress key)
        {
            throw new NotImplementedException();
        }

        public Task UpdateIAmAlive(MembershipEntry entry)
        {
            return Task.CompletedTask;
        }

        public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        {
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Tests for <see cref="SiloHostBuilder"/>.
    /// </summary>
    [TestCategory("BVT")]
    [TestCategory("SiloHostBuilder")]
    public class SiloHostBuilderTests
    {
        /// <summary>
        /// Tests that a silo cannot be created without specifying a ClusterId.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_NoClusterIdTest()
        {
            Assert.Throws<OrleansConfigurationException>(() => new SiloHostBuilder()
                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>())
                .Build());

            var builder = new SiloHostBuilder()
                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                .Configure<ClusterOptions>(options => options.ClusterId = "test")
                .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>());
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
            var builder = new SiloHostBuilder().ConfigureDefaults()
                .UseConfiguration(new ClusterConfiguration())
                .ConfigureServices(RemoveConfigValidatorsAndSetAddress)
                .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>());
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
            var builder = new SiloHostBuilder().ConfigureDefaults()
                .UseConfiguration(new ClusterConfiguration())
                .ConfigureServices(RemoveConfigValidatorsAndSetAddress)
                .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>());
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
            var builder = new SiloHostBuilder().ConfigureDefaults()
                .ConfigureServices(RemoveConfigValidatorsAndSetAddress)
                .UseConfiguration(new ClusterConfiguration())
                .UseConfiguration(new ClusterConfiguration());
            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        /// <summary>
        /// Tests that a silo can be created without specifying configuration.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_NullConfigurationTest()
        {
            var builder = new SiloHostBuilder().ConfigureDefaults()
                .ConfigureServices(RemoveConfigValidatorsAndSetAddress);
            Assert.Throws<ArgumentNullException>(() => builder.UseConfiguration(null));
        }

        /// <summary>
        /// Tests that the <see cref="ISiloHostBuilder.ConfigureServices"/> delegate works as expected.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_ServiceProviderTest()
        {
            var builder = new SiloHostBuilder().ConfigureDefaults()
                .UseConfiguration(new ClusterConfiguration())
                .ConfigureServices(RemoveConfigValidatorsAndSetAddress)
                .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>());

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

        private static void RemoveConfigValidatorsAndSetAddress(IServiceCollection services)
        {
            var validators = services.Where(descriptor => descriptor.ServiceType == typeof(IConfigurationValidator)).ToList();
            foreach (var validator in validators) services.Remove(validator);
            // Configure endpoints because validator is set just before Build()
            services.Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback);
        }

        private class MyService
        {
            public int Id { get; set; }
        }
    }
}