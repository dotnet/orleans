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
    public class LoadTest_Ping : LoadTestBase
    {
        private static readonly string clusterName = "17xcg17_cluster"; // == nightly_build_cluster but more machines allocated to clients and less to servers

        public LoadTest_Ping()
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


        private static int PingLoadTestLength(TimeSpan executionTime, int expectedPerClientTPS)
        {
            // this needs to be a nice round number or the load test framework will throw an exception because parameterized numbers are indivisible.
            return (int)executionTime.TotalSeconds * expectedPerClientTPS;
        }

        private static readonly ClientOptions PingLoadTest_OneSilo_ClientOptions = new ClientOptions()
        {
            ServerCount = 1,
            ClientCount = 8,
            ClientAppName = "GrainBenchmarkLoadTest",
            Number = PingLoadTestLength(TimeSpan.FromMinutes(20), 30000/8),
            Pipeline = 20 * 1000,
        };

        private static readonly ClientOptions PingLoadTest_MultipleSilos_ClientOptions = new ClientOptions()
        {
            ServerCount = 16,
            ClientCount = 16,
            ClientAppName = "GrainBenchmarkLoadTest",
            Number = PingLoadTestLength(TimeSpan.FromMinutes(10), 15000),
            Pipeline = 8 * 1000,
        };

        //[TestMethod, TestCategory("Nightly"), TestCategory("LoadTest"), TestCategory("HaloPresence")]
        [TestMethod, TestCategory("PingPerformance"), TestCategory("Nightly"), TestCategory("LoadTest")]
        public void PingLoadTest_LocalReentrant()
        {
            ClientOptions options = PingLoadTest_OneSilo_ClientOptions.Copy();
            options.AdditionalParameters = new string[] {"-grainType", "LocalReentrant", "-functionType", "PingImmutable", "-grains", "1000"};
            TestLoadScenario(
                "nightly_build",
                clusterName,
                "MetricDefinition3",
                options,
                clientGrammar: "ClientGrammerForNoTPSTracking");
        }

        //[TestMethod, TestCategory("Nightly"), TestCategory("LoadTest"), TestCategory("HaloPresence")]
        [TestMethod, TestCategory("PingPerformance"), TestCategory("Nightly"), TestCategory("LoadTest")]
        public void PingLoadTest_RandomReentrant_MultiSilos()
        {
            ClientOptions options = PingLoadTest_MultipleSilos_ClientOptions.Copy();
            options.AdditionalParameters = new string[] {"-grainType", "RandomReentrant", "-functionType", "PingImmutable", "-grains", "1000"};
            TestLoadScenario(
                "nightly_build",
                clusterName,
                "MetricDefinition3",
                options,
                clientGrammar: "ClientGrammerForNoTPSTracking");
        }

        //[TestMethod, TestCategory("Nightly"), TestCategory("LoadTest"), TestCategory("HaloPresence")]
        //[TestMethod, TestCategory("PingPerformance")]
        [TestMethod]
        public void PingLoadTest_LocalNonReentrant()
        {
            ClientOptions options = PingLoadTest_OneSilo_ClientOptions.Copy();
            options.AdditionalParameters = new string[]{"-grainType", "LocalNonReentrant", "-functionType", "PingImmutable", "-grains", "1000"};
            TestLoadScenario(
                "nightly_build",
                clusterName,
                "MetricDefinition3",
                options,
                clientGrammar: "ClientGrammerForNoTPSTracking");
        }

        //[TestMethod, TestCategory("Nightly"), TestCategory("LoadTest"), TestCategory("HaloPresence")]
        //[TestMethod, TestCategory("PingPerformance")]
        [TestMethod]
        public void PingLoadTest_LocalNonReentrant_WithDelay()
        {
            ClientOptions options = PingLoadTest_OneSilo_ClientOptions.Copy();
            options.AdditionalParameters = new string[]
            {
                "-grainType", "LocalNonReentrant", "-functionType", "PingImmutableWithDelay", "-grains", "1000", "-latency",
                "100"
            };
            TestLoadScenario(
                "nightly_build",
                clusterName,
                "MetricDefinition3",
                options,
                clientGrammar: "ClientGrammerForNoTPSTracking");
        }
    }
}
// ReSharper restore UnusedVariable
