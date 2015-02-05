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
    public class LoadTest_Reliability : LoadTestBase
    {
        public LoadTest_Reliability()
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


        [TestMethod, TestCategory("Nightly"), TestCategory("Reliability")]
        public void Reliability_Load_KillSilo()
        {
            TestFailoverScenario(
                "nightly_build",
                "nightly_build_cluster",
                "MetricDefinitionForReliability",
                new ClientOptions() {ServerCount = 25, ClientCount = 1, ServersPerClient = 5},
                restart: false,
                clientGrammar: "ClientLogForReliability");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Reliability")]
        public void Reliability_Load_RestartSilo()
        {
            TestFailoverScenario(
                "nightly_build",
                "nightly_build_cluster",
                "MetricDefinitionForReliability",
                new ClientOptions() {ServerCount = 25, ClientCount = 1, ServersPerClient = 5},
                restart: true,
                clientGrammar: "ClientLogForReliability");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Reliability")]
        public void Reliability_Load_KillSiloOneAtTime()
        {
            TestFailoverScenario(
                "nightly_build",
                "nightly_build_cluster",
                "MetricDefinitionForReliability",
                new ClientOptions() {ServerCount = 25, ClientCount = 1, ServersPerClient = 5},
                restart: false,
                serversToRestart: 1,
                clientGrammar: "ClientLogForReliability");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("Reliability")]
        public void Reliability_Load_RestartSiloOneAtTime()
        {
            TestFailoverScenario(
                "nightly_build",
                "nightly_build_cluster",
                "MetricDefinitionForReliability",
                new ClientOptions() {ServerCount = 25, ClientCount = 1, ServersPerClient = 5},
                restart: true,
                serversToRestart: 1,
                clientGrammar: "ClientLogForReliability");
        }
    }
}
// ReSharper restore UnusedVariable
