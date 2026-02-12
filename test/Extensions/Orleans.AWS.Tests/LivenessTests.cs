using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;
using UnitTests.MembershipTests;
using Xunit;
using Xunit.Abstractions;

namespace AWSUtils.Tests.Liveness
{
    /// <summary>
    /// Liveness tests for AWS DynamoDB membership provider.
    /// 
    /// These tests verify that Orleans cluster membership management works correctly
    /// when using DynamoDB as the membership table provider. DynamoDB provides:
    /// - Distributed membership tracking across silos
    /// - Failure detection and recovery
    /// - Consistent cluster topology views
    /// 
    /// The tests simulate various failure scenarios to ensure the cluster
    /// maintains consistency and recovers properly.
    /// </summary>
    [TestCategory("Membership"), TestCategory("AWS"), TestCategory("DynamoDb")]
    public class LivenessTests_DynamoDB : LivenessTestsBase
    {
        public LivenessTests_DynamoDB(ITestOutputHelper output) : base(output)
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
                throw new SkipException("Unable to connect to DynamoDB simulator");
        }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
        }

        /// <summary>
        /// Configures silos to use DynamoDB for cluster membership.
        /// Sets up the DynamoDB service endpoint for testing (typically a local simulator).
        /// </summary>
        public class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.UseDynamoDBClustering(options => { options.Service = AWSTestConstants.DynamoDbService; });
            }
        }

        /// <summary>
        /// Configures clients to use DynamoDB for discovering cluster gateways.
        /// Ensures clients can locate and connect to silos using the same
        /// DynamoDB-based membership information.
        /// </summary>
        public class ClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.UseDynamoDBClustering(ob => ob.Configure(gatewayOptions => 
                {
                    gatewayOptions.Service = AWSTestConstants.DynamoDbService;
                }));
            }
        }

        /// <summary>
        /// Basic liveness test verifying cluster membership operations.
        /// Tests that silos can join the cluster, be discovered by other silos,
        /// and maintain accurate membership information in DynamoDB.
        /// </summary>
        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_AWS_DynamoDB_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        /// <summary>
        /// Tests cluster recovery when the primary silo is restarted.
        /// Verifies that the cluster can handle the loss and recovery of
        /// the primary silo without losing membership consistency.
        /// </summary>
        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_AWS_DynamoDB_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        /// <summary>
        /// Tests cluster recovery when a gateway silo is restarted.
        /// Verifies that client connections can recover and find alternative
        /// gateways when their connected gateway fails.
        /// </summary>
        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_AWS_DynamoDB_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        /// <summary>
        /// Tests cluster recovery when a non-primary silo is restarted.
        /// Verifies that grain activations are properly migrated and
        /// the cluster maintains operation during silo failures.
        /// </summary>
        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_AWS_DynamoDB_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        /// <summary>
        /// Tests cluster recovery when a silo with active timers is killed.
        /// Verifies that timer registrations are properly recovered when
        /// grains are reactivated on other silos after failure.
        /// </summary>
        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_AWS_DynamoDB_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
