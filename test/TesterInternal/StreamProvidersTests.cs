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

        private static Guid ServiceId = Guid.NewGuid();
        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(initialSilosCount: 4);
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test1");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test2");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.ErrorInjectionStorageProvider>("ErrorInjector");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("lowercase");
                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                options.ClusterConfiguration.Globals.ServiceId = ServiceId;

                return new TestCluster(options);
            }
        }

        public static readonly string STREAM_PROVIDER_NAME = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
        private readonly ITestOutputHelper output;
        protected TestCluster HostedCluster;

        public StreamProvidersTests_ProviderConfigNotLoaded(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            this.HostedCluster = fixture.HostedCluster;
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
            Guid thisRunServiceId = this.HostedCluster.ClusterConfiguration.Globals.ServiceId;

            SiloHandle siloHandle = this.HostedCluster.GetActiveSilos().First();
            Guid serviceId = await siloHandle.TestHook.GetServiceId();
            Assert.Equal(thisRunServiceId, serviceId);  // "ServiceId active in silo"
          
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("ServiceId")]
        public async Task ServiceId_SiloRestart()
        {
            Guid configServiceId = this.HostedCluster.ClusterConfiguration.Globals.ServiceId;
            output.WriteLine("ServiceId={0}", ServiceId);

            Assert.Equal(ServiceId, configServiceId);  // "ServiceId in test config"

            output.WriteLine("About to reset Silos .....");
            output.WriteLine("Restarting Silos ...");

            foreach (var silo in this.HostedCluster.GetActiveSilos().ToList())
            {
                this.HostedCluster.RestartSilo(silo);
            }
            this.HostedCluster.InitializeClient();

            output.WriteLine("..... Silos restarted");

            output.WriteLine("DeploymentId={0} ServiceId={1}", this.HostedCluster.DeploymentId, this.HostedCluster.ClusterConfiguration.Globals.ServiceId);

            Assert.Equal(ServiceId, this.HostedCluster.ClusterConfiguration.Globals.ServiceId);  // "ServiceId same after restart."

            SiloHandle siloHandle = this.HostedCluster.GetActiveSilos().First();
            Guid serviceId = await siloHandle.TestHook.GetServiceId();
            Assert.Equal(ServiceId, serviceId);  // "ServiceId active in silo"
        }
    }

    public class StreamProvidersTests_ProviderConfigLoaded : OrleansTestingBase, IClassFixture<StreamProvidersTests_ProviderConfigLoaded.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(initialSilosCount: 4);

                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore", numStorageGrains: 1);

                options.ClusterConfiguration.AddAzureTableStorageProvider("AzureStore", deleteOnClear: true);
                options.ClusterConfiguration.AddAzureTableStorageProvider("PubSubStore", deleteOnClear: true, useJsonFormat: false);

                options.ClusterConfiguration.AddSimpleMessageStreamProvider(StreamTestsConstants.SMS_STREAM_PROVIDER_NAME, fireAndForgetDelivery: false);
                options.ClusterConfiguration.AddSimpleMessageStreamProvider("SMSProviderDoNotOptimizeForImmutableData", fireAndForgetDelivery: false, optimizeForImmutableData: false);

                options.ClusterConfiguration.AddAzureQueueStreamProvider(StreamTestsConstants.AZURE_QUEUE_STREAM_PROVIDER_NAME);
                options.ClusterConfiguration.AddAzureQueueStreamProvider("AzureQueueProvider2");

                options.ClusterConfiguration.Globals.MaxMessageBatchingSize = 100;

                return new TestCluster(options);
            }
        }
        private IGrainFactory grainFactory;
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