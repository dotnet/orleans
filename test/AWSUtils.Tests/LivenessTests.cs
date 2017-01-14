using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OrleansAWSUtils.Storage;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
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
                DynamoDBStorage storage;
                try
                {
                    storage = new DynamoDBStorage($"Service=http://localhost:8000", null);
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

        public override TestCluster CreateTestCluster()
        {
            if (!isDynamoDbAvailable.Value)
                throw new SkipException("Unable to connect to DynamoDB simulator");

            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DataConnectionString = "Service=http://localhost:8000;"; ;
            options.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.Custom;
            options.ClusterConfiguration.Globals.MembershipTableAssembly = "OrleansAWSUtils";
            options.ClusterConfiguration.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.Disabled;
            options.ClusterConfiguration.PrimaryNode = null;
            options.ClusterConfiguration.Globals.SeedNodes.Clear();
            return new TestCluster(options);
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
