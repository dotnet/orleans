using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestFramework;

// ReSharper disable UnusedVariable

namespace LoadTest
{
    [TestClass]
    [DeploymentItem("TestConfiguration", "TestConfiguration")] // copy TestConfiguration directory to output directory of same name
    public class LoadTest_Elasticity : LoadTestBase
    {
        private static readonly string CLUSTER_TO_USE = "17xcg16_cluster";
        private static readonly string CLUSTER_TO_USE_FOR_MANY_SILOS = "17xcg16_cluster_many_server";
        private static readonly string METRIC_DEFINITION = "MetricDefinitionForElasticity";
        private static readonly string DEPLOYMENT_CONFIG = "nightly_build";

        public LoadTest_Elasticity()
        {
        }

        [TestInitialize]
        public void Prologue()
        {
            BasePrologue();
        }

        [TestCleanup]
        public void Epilogue()
        {
            BaseEpilogue();
        }


        //-------------------------------------------------------------------------------------------------------------------------------------------------//
        // Silo Configurations
        //-------------------------------------------------------------------------------------------------------------------------------------------------//
        static SiloOptions LoadAwareSiloConfig = new SiloOptions
        {
            PlacementStrategyParameters =
                new PlacementStrategyParameters {
                    DefaultPlacementStrategy = "ActivationCountBasedPlacement", 
                    ActivationCountBasedPlacementChooseOutOf = 2, 
                    DeploymentLoadPublisherRefreshTime = TimeSpan.FromSeconds(1) 
                }
        };

        static SiloOptions RandomSiloConfig = new SiloOptions
        {
            PlacementStrategyParameters =
                new PlacementStrategyParameters
                {
                    DefaultPlacementStrategy = "RandomPlacement",
                    ActivationCountBasedPlacementChooseOutOf = 2,
                    DeploymentLoadPublisherRefreshTime = TimeSpan.FromSeconds(1)
                }
        };


        //-------------------------------------------------------------------------------------------------------------------------------------------------//
        // Client Configurations
        //-------------------------------------------------------------------------------------------------------------------------------------------------//
        ClientOptions ElasticityTest_NewGrainPerRequest_10_6 = new ClientOptions()
        {
            ServerCount = 10,
            ClientCount = 6,
            ServersPerClient = 10,
            ClientAppName = "ElasticityBenchmarkTest",
            Number = 2000000,
            AdditionalParameters = new string[] { "-functionType", "NewGrainPerRequest" }
        };

        ClientOptions ElasticityTest_NewGrainPerRequest_14_8 = new ClientOptions()
        {
            ServerCount = 14,
            ClientCount = 8,
            ServersPerClient = 13,
            ClientAppName = "ElasticityBenchmarkTest",
            Number = 2000000,
            AdditionalParameters = new string[] { "-functionType", "NewGrainPerRequest" }
        };

        ClientOptions ElasticityTest_KeepAlive_25_3 = new ClientOptions()
        {
            ServerCount = 25,
            ClientCount = 3,
            ServersPerClient = 24,
            ClientAppName = "ElasticityBenchmarkTest",
            Number = 30 * 1000 * 1000,
            AdditionalParameters = new string[] { "-grains", "100000", "-functionType", "KeepAlive" },
        };

        // With KeepAlive Requests will be spread initially on -grains untill KEEP_ALIVE_MAX_NUM_REQUESTS_PER_GRAIN is reached
        // and then new garin will be picked. This will cause older grains to be collected.
        // Assumes KEEP_ALIVE_MAX_NUM_REQUESTS_PER_GRAIN is not too high (e.g., 1000 is OK) and that
        // Deactivation AgeLimit is small (600s is OK).
        ClientOptions ElasticityTest_KeepAlive_10_10_With_Collection = new ClientOptions()
        {
            ServerCount = 10,
            ClientCount = 10,
            ServersPerClient = 9,
            ClientAppName = "ElasticityBenchmarkTest",
            Number = 30 * 1000 * 1000,
            AdditionalParameters = new string[] { "-grains", "1000", "-functionType", "KeepAlive" },
        };

        //-------------------------------------------------------------------------------------------------------------------------------------------------//
        // Continous addition of new activations
        // This is to stress-tests the algorithm; we maximize new activations request TPS
        //-------------------------------------------------------------------------------------------------------------------------------------------------//

        //
        // Maximum activation creation, just add new activations.
        // Static setting - no changes
        //
        [TestMethod, TestCategory("Elasticity")]
        public void Elasticity_Add_LoadAware_10_6()
        {
            TestLoadScenario(
                DEPLOYMENT_CONFIG,
                CLUSTER_TO_USE,
                METRIC_DEFINITION,
                ElasticityConfigHelpers.MakeOptions("Add", ElasticityTest_NewGrainPerRequest_10_6),
                clientGrammar: "ClientLogForElasticityFast",
                siloOptions: LoadAwareSiloConfig);
        }

        [TestMethod, TestCategory("Elasticity")]
        public void Elasticity_Add_Random_10_6()
        {
            TestLoadScenario(
                DEPLOYMENT_CONFIG,
                CLUSTER_TO_USE,
                METRIC_DEFINITION,
                ElasticityConfigHelpers.MakeOptions("Add", ElasticityTest_NewGrainPerRequest_10_6),
                clientGrammar: "ClientLogForElasticityFast",
                siloOptions: RandomSiloConfig);
        }

        //
        // Maximum activation creation, just add new activations.
        // Stopping Silos
        //
        [TestMethod, TestCategory("Elasticity")]
        public void Elasticity_Add_Stop_LoadAware_14_8()
        {
            TestGenericDeploymentManipulation(
                DEPLOYMENT_CONFIG,
                CLUSTER_TO_USE,
                METRIC_DEFINITION,
                ElasticityConfigHelpers.MakeOptions("Stop", ElasticityTest_NewGrainPerRequest_14_8),
                manipulateDeployment: stopAction,
                clientGrammar: "ClientLogForElasticityFast",
                siloOptions: LoadAwareSiloConfig);
        }

        [TestMethod, TestCategory("Elasticity")]
        public void Elasticity_Add_Stop_Random_14_8()
        {
            TestGenericDeploymentManipulation(
                DEPLOYMENT_CONFIG,
                CLUSTER_TO_USE,
                METRIC_DEFINITION,
                ElasticityConfigHelpers.MakeOptions("Stop", ElasticityTest_NewGrainPerRequest_14_8),
                manipulateDeployment: stopAction,
                clientGrammar: "ClientLogForElasticityFast",
                siloOptions: RandomSiloConfig);
        }


        //
        // Maximum activation creation, just add new activations.
        // Restarts
        //
        [TestMethod, TestCategory("Elasticity")]
        public void Elasticity_Add_Restart_Random_14_8()
        {
            TestGenericDeploymentManipulation(
                DEPLOYMENT_CONFIG,
                CLUSTER_TO_USE,
                METRIC_DEFINITION,
                ElasticityConfigHelpers.MakeOptions("Restart", ElasticityTest_NewGrainPerRequest_14_8),
                manipulateDeployment: restartAllAction,
                clientGrammar: "ClientLogForElasticityFast",
                siloOptions: RandomSiloConfig);
        }

        [TestMethod, TestCategory("Elasticity")]
        public void Elasticity_Add_Restart_LoadAware_14_8()
        {
            TestGenericDeploymentManipulation(
                DEPLOYMENT_CONFIG,
                CLUSTER_TO_USE,
                METRIC_DEFINITION,
                ElasticityConfigHelpers.MakeOptions("Restart", ElasticityTest_NewGrainPerRequest_14_8),
                manipulateDeployment: restartAllAction,
                clientGrammar: "ClientLogForElasticityFast",
                siloOptions: LoadAwareSiloConfig);
        }

        //-------------------------------------------------------------------------------------------------------------------------------------------------//
        // Rotation of Grains, reusing grains for a (random) amount of messages
        // Static setting. Shows activatioon collection impact.
        //-------------------------------------------------------------------------------------------------------------------------------------------------//
        [TestMethod, TestCategory("Elasticity")]
        public void Elasticity_KeepAlive_LoadAware_Collection_LoadAware_10_10()
        {
            TestLoadScenario(
               DEPLOYMENT_CONFIG,
               CLUSTER_TO_USE,
               METRIC_DEFINITION,
               ElasticityConfigHelpers.MakeOptions("Add", ElasticityTest_KeepAlive_10_10_With_Collection),
               clientGrammar: "ClientLogForElasticityFast",
               siloOptions: LoadAwareSiloConfig);
        }

        [TestMethod, TestCategory("Elasticity")]
        public void Elasticity_KeepAlive_LoadAware_Collection_Random_10_10()
        {
            TestLoadScenario(
               DEPLOYMENT_CONFIG,
               CLUSTER_TO_USE,
               METRIC_DEFINITION,
               ElasticityConfigHelpers.MakeOptions("Add", ElasticityTest_KeepAlive_10_10_With_Collection),
               clientGrammar: "ClientLogForElasticityFast",
               siloOptions: RandomSiloConfig);
        }

        //-------------------------------------------------------------------------------------------------------------------------------------------------//
        // Rotation of Grains, reusing grains for a (random) amount of messages
        // This is to show impact of catch-up time vs. new activations requests
        //-------------------------------------------------------------------------------------------------------------------------------------------------//
        [TestMethod, TestCategory("Elasticity")]
        public void Elasticity_KeepAlive_RestartOne_Random_25_3()
        {
            TestGenericDeploymentManipulation(
                DEPLOYMENT_CONFIG,
                CLUSTER_TO_USE_FOR_MANY_SILOS,
                METRIC_DEFINITION,
                ElasticityConfigHelpers.MakeOptions("RestartOne", ElasticityTest_KeepAlive_25_3),
                manipulateDeployment: restartOneAction,
                clientGrammar: "ClientLogForElasticityFast",
                siloOptions: RandomSiloConfig);
        }

        [TestMethod, TestCategory("Elasticity")]
        public void Elasticity_KeepAlive_RestartOne_LoadAware_25_3()
        {
            TestGenericDeploymentManipulation(
                DEPLOYMENT_CONFIG,
                CLUSTER_TO_USE_FOR_MANY_SILOS,
                METRIC_DEFINITION,
                ElasticityConfigHelpers.MakeOptions("RestartOne", ElasticityTest_KeepAlive_25_3),
                manipulateDeployment: restartOneAction,
                clientGrammar: "ClientLogForElasticityFast",
                siloOptions: LoadAwareSiloConfig);
        }


        //-------------------------------------------------------------------------------------------------------------------------------------------------//

        Action<DeploymentManager, ClientOptions> stopAction =
            new Action<DeploymentManager, ClientOptions>((deployManager, deploymentOptions) =>
            {
                Log.WriteLine(SEV.STATUS, "TEST", "Stopping procedure");
                for (int j = 0; j < (deploymentOptions.ServerCount / 2); j++)
                {
                    SiloHandle silo = deployManager.Silos[j];
                    Log.WriteLine(SEV.STATUS, "TEST", "Action #{0}, Stop SiloName = {1}", j, silo.Name);

                    deployManager.StopSilo(silo);
                    Thread.Sleep(TimeSpan.FromSeconds(90));
                }
            });

        Action<DeploymentManager, ClientOptions> restartOneAction =
            new Action<DeploymentManager, ClientOptions>((deployManager, deploymentOptions) =>
            {
                Log.WriteLine(SEV.STATUS, "TEST", "Restart procedure");
                Thread.Sleep(TimeSpan.FromSeconds(240));
                SiloHandle silo = deployManager.Silos[1];
                Log.WriteLine(SEV.STATUS, "TEST", "Restart SiloName = {0}", silo.Name);
                deployManager.RestartSilo(silo);
            });

        // restart all but one
        Action<DeploymentManager, ClientOptions> restartAllAction =
            new Action<DeploymentManager, ClientOptions>((deployManager, deploymentOptions) =>
            {
                Log.WriteLine(SEV.STATUS, "TEST", "Restart procedure serversToRestart={0}", deployManager.Silos.Count - 1);
                for (int j = 0; j < deployManager.Silos.Count - 1; j++)
                {
                    SiloHandle silo = deployManager.Silos[j];
                    Log.WriteLine(SEV.STATUS, "TEST", "Action #{0}, Restart SiloName = {1}", j, silo.Name);
                    deployManager.RestartSilo(silo);
                    Thread.Sleep(TimeSpan.FromSeconds(60));
                }
            });

        Action<DeploymentManager, ClientOptions> restartGroupAction =
            new Action<DeploymentManager, ClientOptions>((deployManager, deploymentOptions) =>
            {
                Log.WriteLine(SEV.STATUS, "TEST", "Restart procedure serversToRestart={0}, perGroup = 3", deployManager.Silos.Count - 1);
                int restarts = 3;
                int perGroup = 3;
                if (deployManager.Silos.Count < perGroup)
                {
                    throw new Exception("Not enough servers.");
                }

                for (int i = 0; i < restarts; i++)
                {
                    for (int j = 0; j < perGroup; j++)
                    {
                        SiloHandle silo = deployManager.Silos[((i * perGroup) + j) % deployManager.Silos.Count];
                        Log.WriteLine(SEV.STATUS, "TEST", "Action #{0}, Restart SiloName = {1}", j, silo.Name);
                        deployManager.RestartSilo(silo);
                    }
                    Thread.Sleep(TimeSpan.FromSeconds(240));
                }
            });

        /// <summary>
        /// This test set-up allows us to provide a generic deployment manipulation function to start/stop/add silos.
        /// </summary>
        private void TestGenericDeploymentManipulation(
           string deploymentConfig,
           string clusterName,
           string metricCollector,
           ClientOptions deploymentOptions,
           Action<DeploymentManager, ClientOptions> manipulateDeployment,
           string clientGrammar = "ClientLog",
           string serverGrammar = "ServerLog",
           SiloOptions siloOptions = null)
        {
            DumpTestOptions(deploymentConfig, metricCollector, deploymentOptions);

            //--- STEP 0: Clean up ---
            var dc = testConfig.GetDeploymentConfig(deploymentConfig, clusterName);
            dc.SiloOptions = siloOptions;
            dc.SelectClient(deploymentOptions.ClientAppName);
            DeploymentManager deployManager = new DeploymentManager(dc);
            deployManager.CleanUp();

            try
            {
                ParserGrammar svrGrmr = testConfig.GetGrammar(serverGrammar);
                ParserGrammar grammar = testConfig.GetGrammar(clientGrammar);
                MetricCollector collector = testConfig.GetMetricCollector(metricCollector);

                //--- STEP 1- 4: Start test environment
                deployManager.StartTestEnvironment(deploymentOptions, svrGrmr, grammar, collector);
                collector.BeginAnalysis();

                //--- STEP 5: Start Background tasks ---
                Task[] babySittingTasks = new Task[] 
                {
                    Task.Factory.StartNew(()=> 
                    {
                        collector.ProcessInBackground();
                    }),
                    // Task B : Keep watch on server processes.
                    Task.Factory.StartNew(()=> 
                    {
                        deployManager.BabysitSilos();
                    }),
                    // Task C : Keep watch on client processes.
                    Task.Factory.StartNew(()=> 
                    {
                        deployManager.BabysitClients();
                    }),
                    // Task D : Wait until one of the client finishes successfully and then stop metric collection.
                    Task.Factory.StartNew(()=> 
                    {
                        //wait for ANY one client to finish.
                        QuickParser.WaitForStateAny(deployManager.Logs, "Finished");
                        // stop aggregating and asserting now onwards
                        collector.EndAnalysis();
                    }),
                };

                // Do something with the deployment (e.g., start/stop silos)
                manipulateDeployment(deployManager, deploymentOptions);

                //--- STEP 6: Wait until background processing is completed successfully or exception is thrown ---
                int indx1 = Task.WaitAny(babySittingTasks);
                if (babySittingTasks[indx1].IsFaulted)
                {
                    deployManager.TestFinished();
                    collector.EndAnalysis();
                    TaskHelper.LogTaskFailuresAndThrow(babySittingTasks);
                }

                //--- STEP 7: Wait for all client to finish ---
                if (!collector.ExitEarly)
                {
                    QuickParser.WaitForStateAll(deployManager.Logs, "Finished");
                }
                deployManager.TestFinished();
            }
            catch (Exception ex)
            {

                Log.WriteLine(SEV.ERROR, "TestResults", "Test Failed with exception:{0}", ex);
                Log.WriteLine(SEV.STATUS, "TEST", "----------DUMPING AZURE TABLE---------");
                deployManager.DumpAzureTablePartition();
                Log.WriteLine(SEV.STATUS, "TEST", "******************");
                throw new AggregateException(ex);
            }
            finally
            {
                //--- STEP 8: Cleanup and save logs ---
                deployManager.CleanUp();
                deployManager.SaveLogs();
                TaskHelper.CheckUnobservedExceptions();
            }
        }
    }
}
// ReSharper restore UnusedVariable
