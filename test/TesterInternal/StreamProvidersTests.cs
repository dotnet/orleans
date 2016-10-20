﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Orleans;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.StreamingTests;
using UnitTests.Tester;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.Streaming
{
    public class StreamProvidersTests_ProviderConfigNotLoaded : OrleansTestingBase, IClassFixture<StreamProvidersTests_ProviderConfigNotLoaded.Fixture>
    {
        public class Fixture : BaseClusterFixture
        {
            public static Guid ServiceId = Guid.NewGuid();
            private static readonly FileInfo SiloConfig = new FileInfo("Config_DevStorage.xml");
            public static TestingSiloOptions SiloOptions = new TestingSiloOptions
            {
                SiloConfigFile = SiloConfig,
                AdjustConfig = config =>
                {
                    config.Globals.ServiceId = ServiceId;
                }
            };

            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(SiloOptions);
            }
        }

        protected TestingSiloHost HostedCluster { get; private set; }
        private TestingSiloOptions SiloOptions;
        private Guid ServiceId;
        public static readonly string STREAM_PROVIDER_NAME = StreamTestsConstants.SMS_STREAM_PROVIDER_NAME;
        private readonly ITestOutputHelper output;

        public StreamProvidersTests_ProviderConfigNotLoaded(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            HostedCluster = fixture.HostedCluster;
            SiloOptions = Fixture.SiloOptions;
            ServiceId = Fixture.ServiceId;
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Providers")]
        public void ProvidersTests_ConfigNotLoaded()
        {
            bool hasThrown = false;
            Guid streamId = Guid.NewGuid();
            var grainFullName = typeof(Streaming_ConsumerGrain).FullName;
            // consumer joins first, producer later
            IStreaming_ConsumerGrain consumer = GrainClient.GrainFactory.GetGrain<IStreaming_ConsumerGrain>(Guid.NewGuid(), grainFullName);
            try
            {
                consumer.BecomeConsumer(streamId, STREAM_PROVIDER_NAME, null).Wait();
            }
            catch (Exception exc)
            {
                hasThrown = true;
                Exception baseException = exc.GetBaseException();
                Assert.Equal(typeof(KeyNotFoundException), baseException.GetType());
            }
            Assert.True(hasThrown, "Should have thrown.");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("ServiceId"), TestCategory("Providers")]
        public void ServiceId_ProviderRuntime()
        {
            Guid thisRunServiceId = this.HostedCluster.Globals.ServiceId;

            SiloHandle siloHandle = this.HostedCluster.GetActiveSilos().First();
            Guid serviceId = siloHandle.TestHook.ServiceId;
            Assert.Equal(thisRunServiceId, serviceId);  // "ServiceId active in silo"

            // ServiceId is not currently available in client config
            //serviceId = ClientProviderRuntime.Instance.GetServiceId();
            //Assert.Equal(thisRunServiceId, serviceId);  // "ServiceId active in client"
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("ServiceId")]
        public void ServiceId_SiloRestart()
        {
            Guid configServiceId = this.HostedCluster.Globals.ServiceId;

            var initialDeploymentId = this.HostedCluster.DeploymentId;
            output.WriteLine("DeploymentId={0} ServiceId={1}", this.HostedCluster.DeploymentId, ServiceId);

            Assert.Equal(ServiceId, configServiceId);  // "ServiceId in test config"

            output.WriteLine("About to reset Silos .....");
            output.WriteLine("Stopping Silos ...");
            this.HostedCluster.StopDefaultSilos();
            output.WriteLine("Starting Silos ...");
            this.HostedCluster.RedeployTestingSiloHost(SiloOptions);
            output.WriteLine("..... Silos restarted");

            output.WriteLine("DeploymentId={0} ServiceId={1}", this.HostedCluster.DeploymentId, this.HostedCluster.Globals.ServiceId);

            Assert.Equal(ServiceId, this.HostedCluster.Globals.ServiceId);  // "ServiceId same after restart."
            Assert.NotEqual(initialDeploymentId, this.HostedCluster.DeploymentId);  // "DeploymentId different after restart."

            SiloHandle siloHandle = this.HostedCluster.GetActiveSilos().First();
            Guid serviceId = siloHandle.TestHook.ServiceId;
            Assert.Equal(ServiceId, serviceId);  // "ServiceId active in silo"

            // ServiceId is not currently available in client config
            //serviceId = ClientProviderRuntime.Instance.GetServiceId();
            //Assert.Equal(initialServiceId, serviceId);  // "ServiceId active in client"
        }
    }

    public class StreamProvidersTests_ProviderConfigLoaded : OrleansTestingBase, IClassFixture<StreamProvidersTests_ProviderConfigLoaded.Fixture>
    {
        public class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
                {
                    SiloConfigFile = new FileInfo("Config_StreamProviders.xml")
                });
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming"), TestCategory("Providers")]
        public void ProvidersTests_ProviderWrongName()
        {
            bool hasThrown = false;
            Guid streamId = Guid.NewGuid();
            var grainFullName = typeof(Streaming_ConsumerGrain).FullName;
            // consumer joins first, producer later
            IStreaming_ConsumerGrain consumer = GrainClient.GrainFactory.GetGrain<IStreaming_ConsumerGrain>(Guid.NewGuid(), grainFullName);
            try
            {
                consumer.BecomeConsumer(streamId, "WrongProviderName", null).Wait();
            }
            catch (Exception exc)
            {
                Exception baseException = exc.GetBaseException();
                Assert.Equal(typeof(KeyNotFoundException), baseException.GetType());
            }
            hasThrown = true;
            Assert.True(hasThrown);
        }
    }
}