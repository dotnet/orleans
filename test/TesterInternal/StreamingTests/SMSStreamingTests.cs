using System;
using System.IO;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.Providers.Streams.SimpleMessageStream;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using UnitTests.Tester;
using Xunit;
using Tester;

namespace UnitTests.StreamingTests
{
    public class SMSStreamingTests : OrleansTestingBase, IClassFixture<SMSStreamingTests.Fixture>
    {
        public class Fixture : BaseClusterFixture
        {
            private static Guid serviceId = Guid.NewGuid();
            public static FileInfo SiloConfig = new FileInfo("Config_StreamProviders.xml");
            public static FileInfo ClientConfig = new FileInfo("ClientConfig_StreamProviders.xml");

            //private static readonly TestingSiloOptions smsSiloOption_OnlyPrimary = new TestingSiloOptions
            //            {
            //                StartFreshOrleans = true,
            //                SiloConfigFile = SiloConfigFile,
            //                StartSecondary = false,
            //                AdjustConfig = config => { config.Globals.ServiceId = serviceId; }
            //            };

            protected override TestingSiloHost CreateClusterHost()
            {
                return new TestingSiloHost(new TestingSiloOptions
                {
                    StartFreshOrleans = true,
                    SiloConfigFile = SiloConfig,
                    AdjustConfig = config => { config.Globals.ServiceId = serviceId; }
                },
                //smsSiloOption_OnlyPrimary,
                new TestingClientOptions
                {
                    ClientConfigFile = ClientConfig
                });
            }
        }

        private SingleStreamTestRunner runner;
        private bool fireAndForgetDeliveryProperty;
        
        public SMSStreamingTests(Fixture fixture)
        {
            runner = new SingleStreamTestRunner(SingleStreamTestRunner.SMS_STREAM_PROVIDER_NAME);
            // runner = new SingleStreamTestRunner(SingleStreamTestRunner.SMS_STREAM_PROVIDER_NAME, 0, false);
            fireAndForgetDeliveryProperty = ExtractFireAndForgetDeliveryProperty();
        }

        #region Simple Message Stream Tests

        //------------------------ One to One ----------------------//

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_01_OneProducerGrainOneConsumerGrain()
        {
            await runner.StreamTest_01_OneProducerGrainOneConsumerGrain();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_02_OneProducerGrainOneConsumerClient()
        {
            await runner.StreamTest_02_OneProducerGrainOneConsumerClient();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_03_OneProducerClientOneConsumerGrain()
        {
            await runner.StreamTest_03_OneProducerClientOneConsumerGrain();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_04_OneProducerClientOneConsumerClient()
        {
            await runner.StreamTest_04_OneProducerClientOneConsumerClient();
        }

        //------------------------ MANY to Many different grains ----------------------//

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            await runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            await runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients();
        }

        //------------------------ MANY to Many Same grains ----------------------//
        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_09_ManySame_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_10_ManySame_ManyConsumerGrainsManyProducerGrains()
        {
            await runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            await runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains();
        }

        //------------------------ MANY to Many producer consumer same grain ----------------------//

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_13_SameGrain_ConsumerFirstProducerLater()
        {
            await runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(!fireAndForgetDeliveryProperty);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_14_SameGrain_ProducerFirstConsumerLater()
        {
            await runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(!fireAndForgetDeliveryProperty);
        }

        private bool ExtractFireAndForgetDeliveryProperty()
        {
            ClusterConfiguration orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(Fixture.SiloConfig.FullName);
            ProviderCategoryConfiguration providerConfigs = orleansConfig.Globals.ProviderConfigurations["Stream"];
            IProviderConfiguration provider = providerConfigs.Providers[SingleStreamTestRunner.SMS_STREAM_PROVIDER_NAME];

            string fireAndForgetProperty = null;
            bool fireAndForget = false;
            if (provider.Properties.TryGetValue(SimpleMessageStreamProvider.FIRE_AND_FORGET_DELIVERY, out fireAndForgetProperty))
            {
                fireAndForget = Boolean.Parse(fireAndForgetProperty);
            }
            return fireAndForget;
        }

        //----------------------------------------------//

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_15_ConsumeAtProducersRequest()
        {
            await runner.StreamTest_15_ConsumeAtProducersRequest();
        }

        //[Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task SMS_16_Deactivation_OneProducerGrainOneConsumerGrain()
        {
            await runner.StreamTest_16_Deactivation_OneProducerGrainOneConsumerGrain();
        }

        //public async Task SMS_17_Persistence_OneProducerGrainOneConsumerGrain()
        //{
        //    await runner.StreamTest_17_Persistence_OneProducerGrainOneConsumerGrain();
        //}

        [Fact, TestCategory("Streaming"), TestCategory("Functional")]
        public async Task SMS_19_ConsumerImplicitlySubscribedToProducerClient()
        {
            await runner.StreamTest_19_ConsumerImplicitlySubscribedToProducerClient();
        }

        [Fact, TestCategory("Streaming"), TestCategory("Functional")]
        public async Task SMS_20_ConsumerImplicitlySubscribedToProducerGrain()
        {
            await runner.StreamTest_20_ConsumerImplicitlySubscribedToProducerGrain();
        }

        [Fact(Skip = "Ignore"), TestCategory("Streaming"), TestCategory("Failures")]
        public async Task SMS_21_GenericConsumerImplicitlySubscribedToProducerGrain()
        {
            await runner.StreamTest_21_GenericConsumerImplicitlySubscribedToProducerGrain();
        }

        [Fact, TestCategory("Streaming"), TestCategory("Functional")]
        public async Task StreamTest_22_TestImmutabilityDuringStreaming()
        {
            await runner.StreamTest_22_TestImmutabilityDuringStreaming();
        }

        #endregion Simple Message Stream Tests
    }
}