using AWSUtils.Tests.StorageTests;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using System.Threading.Tasks;
using UnitTests.MembershipTests;
using Xunit;
using Xunit.Abstractions;

namespace AWSUtils.Tests.Liveness
{
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

        public class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.UseDynamoDBClustering(options => { options.Service = AWSTestConstants.DynamoDbService; });
            }
        }

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

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_AWS_DynamoDB_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_AWS_DynamoDB_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_AWS_DynamoDB_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_AWS_DynamoDB_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Liveness_AWS_DynamoDB_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
