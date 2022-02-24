using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.Redis;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.TestingHost;
using StackExchange.Redis;
using TestExtensions;
using UnitTests.GrainInterfaces.Directories;
using UnitTests.Grains.Directories;
using Xunit;

namespace Tester.Directories
{
    [TestCategory("Azure")]
    public class AzureMultipleGrainDirectoriesTests : MultipleGrainDirectoriesTests
    {
        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                siloBuilder.AddAzureTableGrainDirectory(
                    CustomDirectoryGrain.DIRECTORY,
                    options => options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString));
            }
        }

        protected override void CheckPreconditionsOrThrow() => TestUtils.CheckForAzureStorage();

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            base.ConfigureTestCluster(builder);
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }
    }

    [TestCategory("Redis")]
    public class RedisMultipleGrainDirectoriesTests : MultipleGrainDirectoriesTests
    {
        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                siloBuilder
                    .AddRedisGrainDirectory(
                        CustomDirectoryGrain.DIRECTORY,
                        options =>
                        {
                            options.ConfigurationOptions = ConfigurationOptions.Parse(TestDefaultConfiguration.RedisConnectionString);
                            options.EntryExpiry = TimeSpan.FromMinutes(5);
                        })
                    .ConfigureLogging(builder => builder.AddFilter(typeof(RedisGrainDirectory).FullName, LogLevel.Debug));

            }
        }

        protected override void CheckPreconditionsOrThrow()
        {
            if (string.IsNullOrWhiteSpace(TestDefaultConfiguration.RedisConnectionString))
            {
                throw new SkipException("TestDefaultConfiguration.RedisConnectionString is empty");
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            base.ConfigureTestCluster(builder);
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }
    }

    [TestCategory("Functionals"), TestCategory("Directory")]
    public class MockMultipleGrainDirectoriesTests : MultipleGrainDirectoriesTests
    {
        private class MockGrainDirectory : IGrainDirectory
        {
            private readonly Dictionary<string, GrainAddress> _mapping = new();

            private readonly SemaphoreSlim _lock = new(1);

            private int _lookupCounter = 0;
            private int _registerCounter = 0;
            private int _unregisterCounter = 0;

            public int LookupCounter => _lookupCounter;
            public int RegisterCounter => _registerCounter;
            public int UnregisterCounter => _unregisterCounter;

            public async Task<GrainAddress> Lookup(string grainId)
            {
                try
                {
                    await _lock.WaitAsync();
                    _lookupCounter++;
                    return _mapping.TryGetValue(grainId, out var address) ? address : null;
                }
                finally
                {
                    _lock.Release();
                }
            }

            public async Task<GrainAddress> Register(GrainAddress address)
            {
                try
                {
                    await _lock.WaitAsync();
                    _registerCounter++;
                    if (_mapping.TryGetValue(address.GrainId, out var existingAddress))
                    {
                        return existingAddress;
                    }
                    _mapping.Add(address.GrainId, address);
                    return address;
                }
                finally
                {
                    _lock.Release();
                }
            }

            public async Task Unregister(GrainAddress address)
            {
                try
                {
                    await _lock.WaitAsync();
                    _unregisterCounter++;
                    if (_mapping.TryGetValue(address.GrainId, out var existingAddress) && existingAddress.Equals(address))
                    {
                        _mapping.Remove(address.GrainId);
                    }
                }
                finally
                {
                    _lock.Release();
                }
            }

            public Task UnregisterSilos(List<string> siloAddresses) => Task.CompletedTask;
        }

        private static MockGrainDirectory GrainDirectory = new();

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                siloBuilder.ConfigureServices(svc => svc.AddSingletonNamedService<IGrainDirectory>(CustomDirectoryGrain.DIRECTORY, (sp, name) => GrainDirectory));
            }
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            base.ConfigureTestCluster(builder);
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        [Fact]
        public async Task CheckIfDirectoryIsUsed()
        {
            var grainOnPrimary = await GetGrainOnPrimary().WithTimeout(TimeSpan.FromSeconds(5), "Could not get a grain on the primary silo");
            var grainOnSecondary = await GetGrainOnSecondary().WithTimeout(TimeSpan.FromSeconds(5), "Could not get a grain on the secondary silo");

            await grainOnPrimary.ProxyPing(grainOnSecondary);

            Assert.True(GrainDirectory.RegisterCounter > 0);
            Assert.True(GrainDirectory.LookupCounter > 0);
        }
    }

    public abstract class MultipleGrainDirectoriesTests : TestClusterPerTest
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 2;
        }

        [SkippableFact, TestCategory("Directory"), TestCategory("Functionals")]
        public async Task PingGrain()
        {
            var grainOnPrimary = await GetGrainOnPrimary().WithTimeout(TimeSpan.FromSeconds(5), "Could not get a grain on the primary silo");
            var grainOnSecondary = await GetGrainOnSecondary().WithTimeout(TimeSpan.FromSeconds(5), "Could not get a grain on the secondary silo");

            // Setup
            var primaryCounter = await grainOnPrimary.Ping();
            var secondaryCounter = await grainOnSecondary.Ping();

            // Each silo see the activation on the other silo
            Assert.Equal(++primaryCounter, await grainOnSecondary.ProxyPing(grainOnPrimary));
            Assert.Equal(++secondaryCounter, await grainOnPrimary.ProxyPing(grainOnSecondary));

            await Task.Delay(5000);

            // Shutdown the secondary silo
            await this.HostedCluster.StopSecondarySilosAsync();

            // Activation on the primary silo should still be there, another activation should be
            // created for the other one
            Assert.Equal(++primaryCounter, await grainOnPrimary.Ping());
            Assert.Equal(1, await grainOnSecondary.Ping());
        }

        protected async Task<ICustomDirectoryGrain> GetGrainOnPrimary()
        {
            while (true)
            {
                var grain = this.GrainFactory.GetGrain<ICustomDirectoryGrain>(Guid.NewGuid());
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(HostedCluster.Primary.SiloAddress.Endpoint.ToString()))
                    return grain;
            }
        }

        protected async Task<ICustomDirectoryGrain> GetGrainOnSecondary()
        {
            while (true)
            {
                var grain = this.GrainFactory.GetGrain<ICustomDirectoryGrain>(Guid.NewGuid());
                var instanceId = await grain.GetRuntimeInstanceId();
                if (instanceId.Contains(HostedCluster.SecondarySilos[0].SiloAddress.Endpoint.ToString()))
                    return grain;
            }
        }
    }
}
