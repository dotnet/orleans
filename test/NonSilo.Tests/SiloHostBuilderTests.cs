using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Orleans;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Configuration.Validators;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Statistics;
using TestGrainInterfaces;
using UnitTests.DtosRefOrleans;
using UnitTests.Grains;
using Xunit;

namespace NonSilo.Tests
{
    public class NoOpMembershipTable : IMembershipTable
    {
        public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate)
        {
            return Task.CompletedTask;
        }

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
        [Fact]
        public void SiloBuilderTest()
        {
            var host = new HostBuilder()
                .UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .UseLocalhostClustering()
                        .Configure<ClusterOptions>(options => options.ClusterId = "someClusterId")
                        .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback);
                })
                .UseDefaultServiceProvider((context, options) =>
                {
                    options.ValidateScopes = true;
                    options.ValidateOnBuild = true;
                })
                .Build();

            var clusterClient = host.Services.GetRequiredService<IClusterClient>();
        }

        /// <summary>
        /// Tests that a silo cannot be created without specifying a ClusterId and a ServiceId.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_ClusterOptionsTest()
        {
            Assert.Throws<OrleansConfigurationException>(() => new SiloHostBuilder()
                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>())
                .Build());

            Assert.Throws<OrleansConfigurationException>(() => new SiloHostBuilder()
                .Configure<ClusterOptions>(options => options.ClusterId = "someClusterId")
                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>())
                .Build());

            Assert.Throws<OrleansConfigurationException>(() => new SiloHostBuilder()
                .Configure<ClusterOptions>(options => options.ServiceId = "someServiceId")
                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>())
                .Build());

            var builder = new SiloHostBuilder()
                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                .Configure<ClusterOptions>(options => { options.ClusterId = "someClusterId"; options.ServiceId = "someServiceId"; })
                .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>());
            using (var silo = builder.Build())
            {
                Assert.NotNull(silo);
            }
        }

        /// <summary>
        /// Grain's CollectionAgeLimit must be > 0 minutes.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_GrainCollectionOptionsForZeroSecondsAgeLimitTest()
        {
            Assert.Throws<OrleansConfigurationException>(() => new SiloHostBuilder()
                .Configure<ClusterOptions>(options => { options.ClusterId = "GrainCollectionClusterId"; options.ServiceId = "GrainCollectionServiceId"; })
                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>())
                .Configure<GrainCollectionOptions>(options => options
                            .ClassSpecificCollectionAge
                            .Add(typeof(CollectionSpecificAgeLimitForZeroSecondsActivationGcTestGrain).FullName, TimeSpan.Zero))
               .Build());
        }

        /// <summary>
        /// Tests that a silo can be created without specifying configuration.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_NoSpecifiedConfigurationTest()
        {
            var builder = new SiloHostBuilder()
                .ConfigureDefaults()
                .UseLocalhostClustering()
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
                .ConfigureServices(RemoveConfigValidatorsAndSetAddress)
                .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>());
            using (builder.Build())
            {
                Assert.Throws<InvalidOperationException>(() => builder.Build());
            }
        }

        /// <summary>
        /// Tests that the <see cref="ISiloHostBuilder.ConfigureServices"/> delegate works as expected.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_ServiceProviderTest()
        {
            var builder = new SiloHostBuilder()
                .ConfigureDefaults()
                .UseLocalhostClustering()
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

        /// <summary>
        /// Ensures <see cref="LoadSheddingValidator"/> passes when LoadSheddingEnabled is false.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_LoadSheddingValidatorPassesWhenLoadSheddingDisabled()
        {
            var builder = new SiloHostBuilder().ConfigureDefaults()
                    .UseLocalhostClustering()
                    .Configure<ClusterOptions>(options => options.ClusterId = "someClusterId")
                    .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                    .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>())
                    .Configure<LoadSheddingOptions>(options =>
                    {
                        options.LoadSheddingEnabled = false;
                        options.LoadSheddingLimit = 95;
                    })
                    .ConfigureServices(svcCollection =>
                    {
                        svcCollection.AddSingleton<FakeHostEnvironmentStatistics>();
                        svcCollection.AddFromExisting<IHostEnvironmentStatistics, FakeHostEnvironmentStatistics>();
                        svcCollection.AddTransient<IConfigurationValidator, LoadSheddingValidator>();
                    });

            using (var host = builder.Build())
            {
                Assert.NotNull(host);
            }
        }

        /// <summary>
        /// Ensures <see cref="LoadSheddingValidator"/> fails when LoadSheddingLimit greater than 100.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_LoadSheddingValidatorAbove100ShouldFail()
        {
            Assert.Throws<OrleansConfigurationException>(() =>
                    new SiloHostBuilder()
                        .ConfigureDefaults()
                        .UseLocalhostClustering()
                        .Configure<ClusterOptions>(options => options.ClusterId = "someClusterId")
                        .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                        .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>())
                        .Configure<LoadSheddingOptions>(options =>
                        {
                            options.LoadSheddingEnabled = true;
                            options.LoadSheddingLimit = 101;
                        })
                        .ConfigureServices(svcCollection =>
                        {
                            svcCollection.AddSingleton<FakeHostEnvironmentStatistics>();
                            svcCollection.AddFromExisting<IHostEnvironmentStatistics, FakeHostEnvironmentStatistics>();
                            svcCollection.AddTransient<IConfigurationValidator, LoadSheddingValidator>();
                        })
                        .Build());
        }

        /// <summary>
        /// Ensures <see cref="LoadSheddingValidator"/> fails validation when invalid/no instance of
        /// <see cref="IHostEnvironmentStatistics"/> is registered using otherwise valid <see cref="LoadSheddingOptions"/>.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_LoadSheddingValidatorFailsWithNoRegisteredHostEnvironmentStatistics()
        {
            Assert.Throws<OrleansConfigurationException>(() =>
                new SiloHostBuilder()
                    .ConfigureDefaults()
                    .UseLocalhostClustering()
                    .Configure<ClusterOptions>(options => options.ClusterId = "someClusterId")
                    .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                    .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>())
                    .Configure<LoadSheddingOptions>(options =>
                    {
                        options.LoadSheddingEnabled = true;
                        options.LoadSheddingLimit = 95;
                    }).ConfigureServices(svcCollection =>
                    {
                        svcCollection.AddTransient<IConfigurationValidator, LoadSheddingValidator>();
                    })
                    .Build()); 
        }

        /// <summary>
        /// The <see cref="LoadSheddingValidator"/> should pass validation with appropriate values.
        /// </summary>
        [Fact]
        public void SiloHostBuilder_LoadSheddingValidatorPasses()
        {
            var builder = new SiloHostBuilder()
                .ConfigureDefaults()
                .UseLocalhostClustering()
                .Configure<ClusterOptions>(options => options.ClusterId = "someClusterId")
                .Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback)
                .ConfigureServices(services => services.AddSingleton<IMembershipTable, NoOpMembershipTable>())
                .Configure<LoadSheddingOptions>(options =>
                {
                    options.LoadSheddingEnabled = true;
                    options.LoadSheddingLimit = 95;
                })
                .ConfigureServices(svcCollection =>
                {
                    svcCollection.AddSingleton<FakeHostEnvironmentStatistics>();
                    svcCollection.AddFromExisting<IHostEnvironmentStatistics, FakeHostEnvironmentStatistics>();
                    svcCollection.AddTransient<IConfigurationValidator, LoadSheddingValidator>();
                });

            using (var host = builder.Build())
            {
                Assert.NotNull(host);
            }
        }

        [Fact]
        public async Task SiloBuilderThrowsDuringStartupIfNoGrainsAdded()
        {
            var host = new HostBuilder()
                .UseOrleans(siloBuilder =>
                {
                    // Add only an assembly with generated serializers but no grain interfaces or grain classes
                    siloBuilder.UseLocalhostClustering()
                    .ConfigureApplicationParts(parts =>
                    {
                        parts.ClearApplicationParts();
                    });
                }).Build();

            await Assert.ThrowsAsync<OrleansConfigurationException>(() => host.StartAsync());
        }

        private static void RemoveConfigValidatorsAndSetAddress(IServiceCollection services)
        {
            var validators = services.Where(descriptor => descriptor.ServiceType == typeof(IConfigurationValidator)).ToList();
            foreach (var validator in validators) services.Remove(validator);
            // Configure endpoints because validator is set just before Build()
            services.Configure<EndpointOptions>(options => options.AdvertisedIPAddress = IPAddress.Loopback);
        }

        private class FakeHostEnvironmentStatistics : IHostEnvironmentStatistics
        {
            public long? TotalPhysicalMemory => 0;

            public float? CpuUsage => 0;

            public long? AvailableMemory => 0;
        }

        private class MyService
        {
            public int Id { get; set; }
        }
    }
}