using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnitTests.MembershipTests;
using Xunit;
using Xunit.Abstractions;

namespace AWSUtils.Tests.Liveness
{
    [TestCategory("Membership"), TestCategory("AWS"), TestCategory("DynamoDb")]
    public class LivenessTests_DynamoDB : LivenessTestsBase
    {
        private static Lazy<bool> isDynamoDbAvailable = new Lazy<bool>(() =>
        {
            try
            {
                Orleans.AWSUtils.Tests.DynamoDBStorage storage;
                try
                {
                    storage = new Orleans.AWSUtils.Tests.DynamoDBStorage(NullLoggerFactory.Instance.CreateLogger("DynamoDBStorage"), "http://localhost:8000");
                }
                catch (AmazonServiceException)
                {
                    return false;
                }
                storage.InitializeTable("TestTable", new List<KeySchemaElement> {
                    new KeySchemaElement { AttributeName = "PartitionKey", KeyType = KeyType.HASH }
                }, new List<AttributeDefinition> {
                    new AttributeDefinition { AttributeName = "PartitionKey", AttributeType = ScalarAttributeType.S }
                }).WithTimeout(TimeSpan.FromSeconds(2), "Unable to connect to AWS DynamoDB simulator").Wait();
                return true;
            }
            catch (Exception exc)
            {
                if (exc.InnerException is TimeoutException)
                    return false;

                throw;
            }
        });

        public LivenessTests_DynamoDB(ITestOutputHelper output) : base(output)
        {
        }

        public static string Service = "http://localhost:8000";

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            if (!isDynamoDbAvailable.Value)
                throw new SkipException("Unable to connect to DynamoDB simulator");
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
            builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
        }

        public class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.UseDynamoDBClustering(options => { options.Service = Service; });
            }
        }

        public class ClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.UseDynamoDBClustering(ob => ob.Configure(gatewayOptions => 
                {
                    gatewayOptions.Service = $"Service={Service}";
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
