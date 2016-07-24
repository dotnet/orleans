﻿using System.IO;
using System.Threading.Tasks;

using Orleans.Providers.Streams.AzureQueue;
using Orleans.TestingHost;
using UnitTests.StreamingTests;
using UnitTests.Tester;
using System;
using Xunit;

namespace UnitTests.Streaming
{
    public class AQStreamingTests : HostedTestClusterPerTest
    {
        internal static readonly FileInfo SiloConfigFile = new FileInfo("Config_AzureStreamProviders.xml");
        internal static readonly FileInfo ClientConfigFile = new FileInfo("ClientConfig_AzureStreamProviders.xml");

        private static readonly TestingSiloOptions aqSiloOptions = new TestingSiloOptions
            {
                SiloConfigFile = SiloConfigFile,
            };
        private static readonly TestingClientOptions aqClientOptions = new TestingClientOptions
            {
                ClientConfigFile = ClientConfigFile
            };
        //private static readonly TestingSiloOptions aqSiloOption_OnlyPrimary = new TestingSiloOptions
        //    {
        //        SiloConfigFile = SiloConfigFile,
        //        StartSecondary = false,
        //    };
        //private static readonly TestingSiloOptions aqSiloOption_NoClient = new TestingSiloOptions
        //    {
        //        SiloConfigFile = SiloConfigFile,
        //        StartClient = false
        //    };

        private SingleStreamTestRunner runner;

        public override TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(aqSiloOptions, aqClientOptions);
            //return new TestingSiloHost(aqSiloOption_OnlyPrimary, aqClientOptions);
            //return new TestingSiloHost(aqSiloOption_NoClient);
        }
        
        public AQStreamingTests()
        {
            runner = new SingleStreamTestRunner(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME);
        }
        
        public override void Dispose()
        {
            var deploymentId = HostedCluster.DeploymentId;
            base.Dispose();
            AzureQueueStreamProviderUtils.DeleteAllUsedAzureQueues(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, deploymentId, StorageTestConstants.DataConnectionString).Wait();
        }

        ////------------------------ One to One ----------------------//

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_01_OneProducerGrainOneConsumerGrain()
        {
            await runner.StreamTest_01_OneProducerGrainOneConsumerGrain();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_02_OneProducerGrainOneConsumerClient()
        {
            await runner.StreamTest_02_OneProducerGrainOneConsumerClient();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_03_OneProducerClientOneConsumerGrain()
        {
            await runner.StreamTest_03_OneProducerClientOneConsumerGrain();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_04_OneProducerClientOneConsumerClient()
        {
            await runner.StreamTest_04_OneProducerClientOneConsumerClient();
        }

        //------------------------ MANY to Many different grains ----------------------//

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_05_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_06_ManyDifferent_ManyProducerGrainManyConsumerClients()
        {
            await runner.StreamTest_06_ManyDifferent_ManyProducerGrainManyConsumerClients();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_07_ManyDifferent_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_07_ManyDifferent_ManyProducerClientsManyConsumerGrains();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_08_ManyDifferent_ManyProducerClientsManyConsumerClients()
        {
            await runner.StreamTest_08_ManyDifferent_ManyProducerClientsManyConsumerClients();
        }

        //------------------------ MANY to Many Same grains ----------------------//
        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_09_ManySame_ManyProducerGrainsManyConsumerGrains()
        {
            await runner.StreamTest_09_ManySame_ManyProducerGrainsManyConsumerGrains();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_10_ManySame_ManyConsumerGrainsManyProducerGrains()
        {
            await runner.StreamTest_10_ManySame_ManyConsumerGrainsManyProducerGrains();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_11_ManySame_ManyProducerGrainsManyConsumerClients()
        {
            await runner.StreamTest_11_ManySame_ManyProducerGrainsManyConsumerClients();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_12_ManySame_ManyProducerClientsManyConsumerGrains()
        {
            await runner.StreamTest_12_ManySame_ManyProducerClientsManyConsumerGrains();
        }

        //------------------------ MANY to Many producer consumer same grain ----------------------//

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_13_SameGrain_ConsumerFirstProducerLater()
        {
            await runner.StreamTest_13_SameGrain_ConsumerFirstProducerLater(false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_14_SameGrain_ProducerFirstConsumerLater()
        {
            await runner.StreamTest_14_SameGrain_ProducerFirstConsumerLater(false);
        }

        //----------------------------------------------//

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_15_ConsumeAtProducersRequest()
        {
            await runner.StreamTest_15_ConsumeAtProducersRequest();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_16_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 16, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains();
        }

        [Fact, TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_17_MultipleStreams_1J_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 17, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                this.HostedCluster.StartAdditionalSilo);
        }

        //[Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("Streaming")]
        public async Task AQ_18_MultipleStreams_1J_1F_ManyProducerGrainsManyConsumerGrains()
        {
            var multiRunner = new MultipleStreamsTestRunner(SingleStreamTestRunner.AQ_STREAM_PROVIDER_NAME, 18, false);
            await multiRunner.StreamTest_MultipleStreams_ManyDifferent_ManyProducerGrainsManyConsumerGrains(
                this.HostedCluster.StartAdditionalSilo,
                this.HostedCluster.StopSilo);
        }

        [Fact, TestCategory("Streaming")]
        public async Task AQ_19_ConsumerImplicitlySubscribedToProducerClient()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await runner.StreamTest_19_ConsumerImplicitlySubscribedToProducerClient();
        }

        [Fact, TestCategory("Streaming")]
        public async Task AQ_20_ConsumerImplicitlySubscribedToProducerGrain()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await runner.StreamTest_20_ConsumerImplicitlySubscribedToProducerGrain();
        }

        [Fact(Skip = "Ignored"), TestCategory("Streaming"), TestCategory("Failures")]
        public async Task AQ_21_GenericConsumerImplicitlySubscribedToProducerGrain()
        {
            // todo: currently, the Azure queue queue adaptor doesn't support namespaces, so this test will fail.
            await runner.StreamTest_21_GenericConsumerImplicitlySubscribedToProducerGrain();
        }
    }
}