using System;
using System.Collections.Generic;
using System.Globalization;
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
    public class LoadTest : LoadTestBase
    {
        public LoadTest()
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

        [TestMethod, TestCategory("Nightly"), TestCategory("LoadTest"), TestCategory("HaloPresence")]
        public void NightlyLoadTest()
        {
            QuickParser.DEBUG_ONLY_NO_WAITING = false;
            //QuickParser.WAIT_BEFORE_KILLING_SILOS = TimeSpan.FromSeconds(310);
            ClientOptions testOptions = new ClientOptions
            {
                ServerCount = 25,
                ClientCount = 10,
                ClientAppName = "PresenceConsole",
            };
            if (ConfigManager.DoShortLoadTestRun)
            {
                testOptions.Number = 5*1000*1000;
                testOptions.Threads = 8;
                testOptions.Pipeline = 2500;
            }
            TestLoadScenario(
                "nightly_build",
                "nightly_build_cluster", //"17xcg18_cluster", //"nightly_build_cluster"
                "MetricDefinition2",
                testOptions);
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("LoadTest")]
        public void ActivationCollectorStressTest()
        {
            ClientOptions testOptions = new ClientOptions
            {
                ServerCount = 25,
                ClientCount = 10,
                ClientAppName = "PresenceConsole",
                Number = 15*1000*1000, // 15M is about 30 min
                AdditionalParameters = new string[] {"-stages", 10.ToString()},
            };
            TestLoadScenario(
                "nightly_build",
                "nightly_build_cluster",
                "MetricDefinition3",
                testOptions,
                clientGrammar: "ClientGrammerForNoTPSTracking");
        }

        //-------------------------------------------------------------------------------------------------------------------------------------------------//

        private static readonly int PersistenceLoadTest_Servers = 5;
        private static readonly int PersistenceLoadTest_Requests = 200*1000;
        private static readonly int PersistenceLoadTest_Pipeline = PersistenceLoadTest_Servers*100;
        private static readonly string PersistenceLoadTest_AppName = "PersistenceLoadTest";
        private static readonly bool PersistenceLoadTest_FullRun = false;

        //[TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure"), TestCategory("LoadTest")]
        [TestMethod]
        public void PersistenceLoadTest()
        {
            ClientOptions deploymentOptions = new ClientOptions
            {
                Number = PersistenceLoadTest_Requests,
                ClientAppName = PersistenceLoadTest_AppName,
                Pipeline = PersistenceLoadTest_Pipeline,
                DirectTest = false,
            };

            if (PersistenceLoadTest_FullRun)
            {
                deploymentOptions.Number *= 100;
                deploymentOptions.ServerCount = 25;
                deploymentOptions.ClientCount = 10;
            }
            else
            {
                deploymentOptions.ServerCount = PersistenceLoadTest_Servers;
                deploymentOptions.ClientCount = PersistenceLoadTest_Servers < 10 ? PersistenceLoadTest_Servers : 10;
            }

            TestLoadScenario(
                "nightly_build",
                "nightly_build_cluster",
                "MetricDefinition3",
                deploymentOptions,
                clientGrammar: "ClientGrammerForNoTPSTracking"
                );
        }

        //[TestMethod, TestCategory("Nightly"), TestCategory("Persistence"), TestCategory("Azure"), TestCategory("LoadTest")]
        [TestMethod]
        public void PersistenceLoadTest_AzureStorageDirect()
        {
            ClientOptions deploymentOptions = new ClientOptions
            {
                Number = PersistenceLoadTest_Requests,
                ClientAppName = PersistenceLoadTest_AppName,
                Pipeline = PersistenceLoadTest_Pipeline,
                DirectTest = true,
                ServerCount = 1,
            };

            if (PersistenceLoadTest_FullRun)
            {
                deploymentOptions.Number *= 100;
                deploymentOptions.ClientCount = 10;
            }
            else
            {
                deploymentOptions.ClientCount = PersistenceLoadTest_Servers;
                deploymentOptions.Threads = 8;
            }

            TestLoadScenario(
                "nightly_build",
                "nightly_build_cluster",
                "MetricDefinition3",
                deploymentOptions,
                clientGrammar: "ClientGrammerForNoTPSTracking"
                );
        }

        //-------------------------------------------------------------------------------------------------------------------------------------------------//

        public void Soramichi_LoadTest(int _ServerCount = 1, int _ClientCount = 10, int _NumThreads = 8, int _NumRequests = 2*1000*1000)
        {
            QuickParser.DEBUG_ONLY_NO_WAITING = false;
            QuickParser.WAIT_BEFORE_KILLING_SILOS = TimeSpan.FromSeconds(120);
            TestLoadScenario(
                "soramichi_build",
                "soramichi_build_cluster",
                "MetricDefinition2",
                new ClientOptions()
                {
                    ServerCount = _ServerCount,
                    ClientCount = _ClientCount,
                    Number = _NumRequests,
                    ClientAppName = "GrainBenchmarkLoadTest",
                    AdditionalParameters = new string[] { "-t", _NumThreads.ToString() }
                });
        }

        [TestMethod, TestCategory("Scale")]
        public void NightlyLoadScaleTest()
        {
            QuickParser.DEBUG_ONLY_NO_WAITING = false;
            TestLoadScenario(
                "nightly_scale_build",
                "nightly_scale_cluster",
                "MetricDefinition2",
                new ClientOptions()
                {
                    ServerCount = 130,
                    ClientCount = 52,
                    ClientAppName = "PresenceConsole",
                    Number = 30000000
                });
        }

        [TestMethod, TestCategory("NightlyUser")]
        public void NightlyUserLoadTest()
        {
            TestLoadScenario(
                "nightly_build",
                "user_load_test_cluster",
                "MetricDefinition3",
                new ClientOptions()
                {
                    ServerCount = 25,
                    ClientCount = 10,
                    ClientAppName = "UserLoadTest",
                    AdditionalParameters = new string[] { "-mb", "80" }
                },
                clientGrammar: "ClientLogForUserLoadTest");
        }

        [TestMethod]
        public void LocalLoadTest()
        {
            QuickParser.DEBUG_ONLY_NO_WAITING = false;
            TestLoadScenario(
                "nightly_build",
                "empty_cluster",
                "MetricDefinition2",
                new ClientOptions() { ServerCount = 2, ClientCount = 1, ClientAppName = "PresenceConsole" },
                clientGrammar: "ClientLogForUserLoadTest");
        }

        [TestMethod]
        public void RunThisWhenYouMakeChangesToParserCode()
        {
            QuickParser.DEBUG_ONLY_NO_WAITING = false;
            TestOldLogData(
                "nightly_build",
                "empty_cluster",
                "MetricDefinition2",
                new ClientOptions() {ServerCount = 25, ClientCount = 10, ClientAppName = "PresenceConsole"},
                @"\\ORLEANS-BUILD-1\TestLogs\TestResults\SavedLogs\xcgbuild-MSR-XCGBLD-8-4-May-2012-05-23\4",
                "Test_",
                clientGrammar: "ClientLog");
        }

        [TestMethod]
        public void OldNightlyUserLoadTest()
        {
            QuickParser.DEBUG_ONLY_NO_WAITING = false;
            TestOldLogData(
                "nightly_build",
                "nightly_build_cluster",
                "MetricDefinition3",
                new ClientOptions() {ServerCount = 25, ClientCount = 10, ClientAppName = "UserLoadTest"},
                @"\\17xcg1801\C$\TestResults\SavedLogs\cjwill-CJWILLSERVER-2-May-2012-22-06\1",
                "Test_",
                clientGrammar: "ClientLogForUserLoadTest");
        }

         [TestMethod, TestCategory("Reminders"), TestCategory("Nightly")]
        public void RegisterReminderLoadTest()
        {
            int serverCount = 10;
            int clientCount = 10;
            int remindersPerServer = 150000;
            int reminderPoolSize = (remindersPerServer * serverCount) / clientCount;
            QuickParser.DEBUG_ONLY_NO_WAITING = false;
            TestLoadScenario(
                "nightly_build",
                "nightly_build_cluster",
                "MetricDefinition3",
                new ClientOptions
                {
                    ServerCount = serverCount,
                    ClientCount = clientCount,
                    ClientAppName = "NewReminderLoadTest",
                    Number = reminderPoolSize * 10,
                    Threads = 8,
                    Pipeline = 500,
                    AdditionalParameters =
                        new[] 
                        {   
                            "--verbose",
                            "--grain-pool-size", remindersPerServer.ToString(CultureInfo.InvariantCulture), 
                            "--reminder-pool-size", reminderPoolSize.ToString(CultureInfo.InvariantCulture),
                            "--start-barrier-size", clientCount.ToString(CultureInfo.InvariantCulture)
                        }
                },
                siloOptions: new SiloOptions
                {
                    UseMockReminderTable = TimeSpan.FromMilliseconds(50)
                },
                clientGrammar: "ClientGrammerForNoTPSTracking");
        }
    }
}
// ReSharper restore UnusedVariable
