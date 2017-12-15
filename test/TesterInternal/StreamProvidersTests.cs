using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orleans;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.StreamingTests;
using Xunit;
using Xunit.Abstractions;
using System.Threading.Tasks;
using Orleans.Runtime.TestHooks;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace UnitTests.Streaming
{
    public class StreamProvidersTests_ProviderConfigNotLoaded : OrleansTestingBase, IClassFixture<StreamProvidersTests_ProviderConfigNotLoaded.Fixture>
    {

        private static readonly Guid ServiceId = Guid.NewGuid();

        public class Fixture : BaseTestClusterFixture
        {
            public ClusterConfiguration ClusterConfiguration { get; set; }

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 4;
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test1");
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test2");
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.ErrorInjectionStorageProvider>("ErrorInjector");
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("lowercase");
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                    legacy.ClusterConfiguration.Globals.ServiceId = ServiceId;

                    this.ClusterConfiguration = legacy.ClusterConfiguration;
                });
            }
        }

        public static readonly string STREAM_PROVIDER_NAME = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
        private readonly ITestOutputHelper output;
        private readonly Fixture fixture;
        protected TestCluster HostedCluster => this.fixture.HostedCluster;

        public StreamProvidersTests_ProviderConfigNotLoaded(ITestOutputHelper output, Fixture fixture)
        {
            this.fixture = fixture;
            this.output = output;
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Providers")]
        public async Task ProvidersTests_ConfigNotLoaded()
        {
            Guid streamId = Guid.NewGuid();
            var grainFullName = typeof(Streaming_ConsumerGrain).FullName;
            // consumer joins first, producer later
            IStreaming_ConsumerGrain consumer = this.HostedCluster.GrainFactory.GetGrain<IStreaming_ConsumerGrain>(Guid.NewGuid(), grainFullName);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => consumer.BecomeConsumer(streamId, STREAM_PROVIDER_NAME, null));
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("ServiceId"), TestCategory("Providers")]
        public async Task ServiceId_ProviderRuntime()
        {
            Guid thisRunServiceId = this.fixture.ClusterConfiguration.Globals.ServiceId;

            SiloHandle siloHandle = this.HostedCluster.GetActiveSilos().First();
            Guid serviceId = await this.fixture.Client.GetTestHooks(siloHandle).GetServiceId();
            Assert.Equal(thisRunServiceId, serviceId);  // "ServiceId active in silo"
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("ServiceId")]
        public async Task ServiceId_SiloRestart()
        {
            Guid configServiceId = this.fixture.ClusterConfiguration.Globals.ServiceId;
            output.WriteLine("ServiceId={0}", ServiceId);

            Assert.Equal(ServiceId, configServiceId);  // "ServiceId in test config"

            output.WriteLine("About to reset Silos .....");
            output.WriteLine("Restarting Silos ...");

            foreach (var silo in this.HostedCluster.GetActiveSilos().ToList())
            {
                this.HostedCluster.RestartSilo(silo);
            }

            output.WriteLine("..... Silos restarted");

            output.WriteLine("ClusterId={0} ServiceId={1}", this.HostedCluster.ClusterId, this.fixture.ClusterConfiguration.Globals.ServiceId);

            Assert.Equal(ServiceId, this.fixture.ClusterConfiguration.Globals.ServiceId);  // "ServiceId same after restart."

            SiloHandle siloHandle = this.HostedCluster.GetActiveSilos().First();
            Guid serviceId = await this.fixture.Client.GetTestHooks(siloHandle).GetServiceId();
            Assert.Equal(ServiceId, serviceId);  // "ServiceId active in silo"
        }
    }

    public class StreamProvidersTests_ProviderConfigLoaded : OrleansTestingBase, IClassFixture<StreamProvidersTests_ProviderConfigLoaded.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 4;
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore", numStorageGrains: 1);

                    legacy.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, fireAndForgetDelivery: false);
                    legacy.ClusterConfiguration.AddSimpleMessageStreamProvider("SMSProviderDoNotOptimizeForImmutableData",
                        fireAndForgetDelivery: false,
                        optimizeForImmutableData: false);
                });
            }
        }

        private readonly IGrainFactory grainFactory;
        public StreamProvidersTests_ProviderConfigLoaded(Fixture fixture)
        {
            this.grainFactory = fixture.GrainFactory;
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Providers")]
        public async Task ProvidersTests_ProviderWrongName()
        {
            Guid streamId = Guid.NewGuid();
            var grainFullName = typeof(Streaming_ConsumerGrain).FullName;
            // consumer joins first, producer later
            IStreaming_ConsumerGrain consumer = this.grainFactory.GetGrain<IStreaming_ConsumerGrain>(Guid.NewGuid(), grainFullName);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => consumer.BecomeConsumer(streamId, "WrongProviderName", null));
        }
    }
}