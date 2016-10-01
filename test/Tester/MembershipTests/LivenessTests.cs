using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.SqlUtils;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using OrleansAWSUtils.Storage;
using Tester;
using UnitTests.General;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.MembershipTests
{
    public class LivenessTestsBase : TestClusterPerTest
    {
        private readonly ITestOutputHelper output;
        private const int numAdditionalSilos = 1;
        private const int numGrains = 20;

        public LivenessTestsBase(ITestOutputHelper output)
        {
            this.output = output;
        }

        protected async Task Do_Liveness_OracleTest_1()
        {
            output.WriteLine("DeploymentId= {0}", this.HostedCluster.DeploymentId);

            SiloHandle silo3 = this.HostedCluster.StartAdditionalSilo();

            IManagementGrain mgmtGrain = GrainClient.GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);

            Dictionary<SiloAddress, SiloStatus> statuses = await mgmtGrain.GetHosts(false);
            foreach (var pair in statuses)
            {
                output.WriteLine("       ######## Silo {0}, status: {1}", pair.Key, pair.Value);
                Assert.Equal(SiloStatus.Active, pair.Value);
            }
            Assert.Equal(3, statuses.Count);

            IPEndPoint address = silo3.Endpoint;
            output.WriteLine("About to stop {0}", address);
            this.HostedCluster.StopSilo(silo3);

            // TODO: Should we be allowing time for changes to percolate?

            output.WriteLine("----------------");

            statuses = await mgmtGrain.GetHosts(false);
            foreach (var pair in statuses)
            {
                output.WriteLine("       ######## Silo {0}, status: {1}", pair.Key, pair.Value);
                IPEndPoint silo = pair.Key.Endpoint;
                if (silo.Equals(address))
                {
                    Assert.True(pair.Value == SiloStatus.ShuttingDown
                        || pair.Value == SiloStatus.Stopping
                        || pair.Value == SiloStatus.Dead,
                        string.Format("SiloStatus for {0} should now be ShuttingDown or Stopping or Dead instead of {1}", silo, pair.Value));
                }
                else
                {
                    Assert.Equal(SiloStatus.Active, pair.Value);
                }
            }
        }

        protected async Task Do_Liveness_OracleTest_2(int silo2Kill, bool restart = true, bool startTimers = false)
        {
            this.HostedCluster.StartAdditionalSilos(numAdditionalSilos);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            for (int i = 0; i < numGrains; i++)
            {
                await SendTraffic(i + 1, startTimers);
            }

            SiloHandle silo2KillHandle;
            if (silo2Kill == 0)
                silo2KillHandle = this.HostedCluster.Primary;
            else
                silo2KillHandle = this.HostedCluster.SecondarySilos[silo2Kill - 1];

            logger.Info("\n\n\n\nAbout to kill {0}\n\n\n", silo2KillHandle.Endpoint);

            if (restart)
                this.HostedCluster.RestartSilo(silo2KillHandle);
            else
                this.HostedCluster.KillSilo(silo2KillHandle);

            bool didKill = !restart;
            await this.HostedCluster.WaitForLivenessToStabilizeAsync(didKill);

            logger.Info("\n\n\n\nAbout to start sending msg to grain again\n\n\n");

            for (int i = 0; i < numGrains; i++)
            {
                await SendTraffic(i + 1);
            }

            for (int i = numGrains; i < 2 * numGrains; i++)
            {
                await SendTraffic(i + 1);
            }
            logger.Info("======================================================");
        }

        protected async Task Do_Liveness_OracleTest_3()
        {
            List<SiloHandle> moreSilos = this.HostedCluster.StartAdditionalSilos(1);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            await TestTraffic();

            logger.Info("\n\n\n\nAbout to stop a first silo.\n\n\n");
            var siloToStop = this.HostedCluster.SecondarySilos[0];
            this.HostedCluster.StopSilo(siloToStop);

            await TestTraffic();

            logger.Info("\n\n\n\nAbout to re-start a first silo.\n\n\n");
            this.HostedCluster.RestartStoppedSecondarySilo(siloToStop.Name);

            await TestTraffic();

            logger.Info("\n\n\n\nAbout to stop a second silo.\n\n\n");
            this.HostedCluster.StopSilo(moreSilos[0]);

            await TestTraffic();

            logger.Info("======================================================");
        }

        private async Task TestTraffic()
        {
            logger.Info("\n\n\n\nAbout to start sending msg to grain again.\n\n\n");
            // same grains
            for (int i = 0; i < numGrains; i++)
            {
                await SendTraffic(i + 1);
            }
            // new random grains
            for (int i = 0; i < numGrains; i++)
            {
                await SendTraffic(random.Next(10000));
            }
        }

        private async Task SendTraffic(long key, bool startTimers = false)
        {
            try
            {
                ILivenessTestGrain grain = GrainClient.GrainFactory.GetGrain<ILivenessTestGrain>(key);
                Assert.Equal(key, grain.GetPrimaryKeyLong());
                Assert.Equal(key.ToString(CultureInfo.InvariantCulture), await grain.GetLabel());
                await LogGrainIdentity(logger, grain);
                if (startTimers)
                {
                    await grain.StartTimer();
                }
            }
            catch (Exception exc)
            {
                logger.Info("Exception making grain call: {0}", exc);
                throw;
            }
        }

        private async Task LogGrainIdentity(Logger logger, ILivenessTestGrain grain)
        {
            logger.Info("Grain {0}, activation {1} on {2}",
                await grain.GetGrainReference(),
                await grain.GetUniqueId(),
                await grain.GetRuntimeInstanceId());
        }
    }

    public class LivenessTests_MembershipGrain : LivenessTestsBase
    {
        public LivenessTests_MembershipGrain(ITestOutputHelper output) : base(output)
        {
        }

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(2);
            options.ClientConfiguration.PreferedGatewayIndex = 1;
            return new TestCluster(options);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_2_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_3_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_4_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }

        //[Fact, TestCategory("Functional"), TestCategory("Membership")]
        public async Task Liveness_Grain_5_ShutdownRestartZeroLoss()
        {
            await Do_Liveness_OracleTest_3();
        }
    }

    public class LivenessTests_AzureTable : LivenessTestsBase
    {
        public LivenessTests_AzureTable(ITestOutputHelper output) : base(output)
        {
            TestUtils.CheckForAzureStorage();
        }

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DataConnectionString = StorageTestConstants.DataConnectionString;
            options.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable;
            options.ClusterConfiguration.PrimaryNode = null;
            options.ClusterConfiguration.Globals.SeedNodes.Clear();
            return new TestCluster(options);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact, TestCategory("Functional"), TestCategory("Membership"), TestCategory("Azure")]
        public async Task Liveness_Azure_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

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
                if(exc.InnerException is TimeoutException)
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

    public class LivenessTests_ZK : LivenessTestsBase
    {
        public LivenessTests_ZK(ITestOutputHelper output) : base(output)
        {
        }

        public override TestCluster CreateTestCluster()
        {
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DataConnectionString = StorageTestConstants.GetZooKeeperConnectionString();
            options.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.ZooKeeper;
            options.ClusterConfiguration.PrimaryNode = null;
            options.ClusterConfiguration.Globals.SeedNodes.Clear();
            return new TestCluster(options);
        }

        [Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_3_Restart_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact, TestCategory("Membership"), TestCategory("ZooKeeper")]
        public async Task Liveness_ZooKeeper_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }

    public class LivenessTests_SqlServer : LivenessTestsBase
    {
        public const string TestDatabaseName = "OrleansTest";
        public LivenessTests_SqlServer(ITestOutputHelper output) : base(output)
        {
        }
        public override TestCluster CreateTestCluster()
        {
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameSqlServer, TestDatabaseName).Result;
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DataConnectionString = relationalStorage.CurrentConnectionString;
            options.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer;
            options.ClusterConfiguration.PrimaryNode = null;
            options.ClusterConfiguration.Globals.SeedNodes.Clear();
            return new TestCluster(options);
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_SqlServer_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_SqlServer_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_SqlServer_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_SqlServer_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact, TestCategory("Membership"), TestCategory("SqlServer")]
        public async Task Liveness_SqlServer_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }



    public class LivenessTests_PostgreSql : LivenessTestsBase
    {
        public const string TestDatabaseName = "orleanstest";
        public LivenessTests_PostgreSql(ITestOutputHelper output) : base(output)
        {
        }
        public override TestCluster CreateTestCluster()
        {
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNamePostgreSql, TestDatabaseName).Result;
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DataConnectionString = relationalStorage.CurrentConnectionString;
            options.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer;
            options.ClusterConfiguration.Globals.AdoInvariant = AdoNetInvariants.InvariantNamePostgreSql;
            options.ClusterConfiguration.PrimaryNode = null;
            options.ClusterConfiguration.Globals.SeedNodes.Clear();
            return new TestCluster(options);
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task Liveness_PostgreSql_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task Liveness_PostgreSql_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task Liveness_PostgreSql_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task Liveness_PostgreSql_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact, TestCategory("Membership"), TestCategory("PostgreSql")]
        public async Task Liveness_PostgreSql_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }


    public class LivenessTests_MySql : LivenessTestsBase
    {
        public const string TestDatabaseName = "OrleansTest";
        public LivenessTests_MySql(ITestOutputHelper output) : base(output)
        {
        }
        public override TestCluster CreateTestCluster()
        {
            var relationalStorage = RelationalStorageForTesting.SetupInstance(AdoNetInvariants.InvariantNameMySql, TestDatabaseName).Result;
            var options = new TestClusterOptions(2);
            options.ClusterConfiguration.Globals.DataConnectionString = relationalStorage.CurrentConnectionString;
            options.ClusterConfiguration.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer;
            options.ClusterConfiguration.Globals.AdoInvariant = AdoNetInvariants.InvariantNameMySql;
            options.ClusterConfiguration.PrimaryNode = null;
            options.ClusterConfiguration.Globals.SeedNodes.Clear();
            return new TestCluster(options);
        }

        [Fact, TestCategory("Membership"), TestCategory("MySql")]
        public async Task Liveness_MySql_1()
        {
            await Do_Liveness_OracleTest_1();
        }

        [Fact, TestCategory("Membership"), TestCategory("MySql")]
        public async Task Liveness_MySql_2_Restart_Primary()
        {
            await Do_Liveness_OracleTest_2(0);
        }

        [Fact, TestCategory("Membership"), TestCategory("MySql")]
        public async Task Liveness_MySql_3_Restartl_GW()
        {
            await Do_Liveness_OracleTest_2(1);
        }

        [Fact, TestCategory("Membership"), TestCategory("MySql")]
        public async Task Liveness_MySql_4_Restart_Silo_1()
        {
            await Do_Liveness_OracleTest_2(2);
        }

        [Fact, TestCategory("Membership"), TestCategory("MySql")]
        public async Task Liveness_MySql_5_Kill_Silo_1_With_Timers()
        {
            await Do_Liveness_OracleTest_2(2, false, true);
        }
    }
}
