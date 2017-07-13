﻿using AWSUtils.Tests.StorageTests;
using Orleans.Providers.Streams;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using OrleansAWSUtils.Streams;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Tester.StreamingTests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace AWSUtils.Tests.Streaming
{
    public class SQSClientStreamTests : TestClusterPerTest
    {
        private const string SQSStreamProviderName = "SQSProvider";
        private const string StreamNamespace = "SQSSubscriptionMultiplicityTestsNamespace";
        private string StorageConnectionString = AWSTestConstants.DefaultSQSConnectionString;

        private readonly ITestOutputHelper output;
        private readonly ClientStreamTestRunner runner;

        public SQSClientStreamTests(ITestOutputHelper output)
        {
            this.output = output;
            runner = new ClientStreamTestRunner(this.HostedCluster);
        }
        
        public override TestCluster CreateTestCluster()
        {
            if (!AWSTestConstants.IsSqsAvailable)
            {
                throw new SkipException("Empty connection string");
            }

            var deploymentId = Guid.NewGuid().ToString();
            var streamConnectionString = new Dictionary<string, string>
                {
                    { "DataConnectionString",  StorageConnectionString},
                    { "DeploymentId",  deploymentId}
                };
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.AddMemoryStorageProvider("PubSubStore");

            options.ClusterConfiguration.Globals.DeploymentId = deploymentId;
            options.ClientConfiguration.DeploymentId = deploymentId;
            options.ClusterConfiguration.Globals.ClientDropTimeout = TimeSpan.FromSeconds(5);
            options.ClientConfiguration.DataConnectionString = StorageConnectionString;
            options.ClusterConfiguration.Globals.DataConnectionString = StorageConnectionString;
            options.ClusterConfiguration.Globals.RegisterStreamProvider<SQSStreamProvider>(SQSStreamProviderName, streamConnectionString);
            options.ClientConfiguration.RegisterStreamProvider<SQSStreamProvider>(SQSStreamProviderName, streamConnectionString);
            return new TestCluster(options);
        }

        public override void Dispose()
        {
            var deploymentId = HostedCluster.DeploymentId;
            base.Dispose();
            SQSStreamProviderUtils.DeleteAllUsedQueues(SQSStreamProviderName, deploymentId, StorageConnectionString).Wait();
        }

        [SkippableFact, TestCategory("AWS")]
        public async Task SQSStreamProducerOnDroppedClientTest()
        {
            logger.Info("************************ AQStreamProducerOnDroppedClientTest *********************************");
            await runner.StreamProducerOnDroppedClientTest(SQSStreamProviderName, StreamNamespace);
        }
    }
}
