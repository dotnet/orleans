using System;
using System.Collections.Generic;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Runtime;
using Tester;

namespace UnitTests.Tester
{
    /// <summary>
    /// Keep this class as a bridge to the OrleansTestingSilo package, 
    /// because it gives a convenient place to declare all the additional
    /// deployment items required by tests 
    /// - such as the TestGrain assemblies, the client and server config files.
    /// </summary>
    //[DeploymentItem("OrleansConfigurationForTesting.xml")]
    //[DeploymentItem("ClientConfigurationForTesting.xml")]
    //[DeploymentItem("TestGrainInterfaces.dll")]
    //[DeploymentItem("TestGrains.dll")]
    //[DeploymentItem("OrleansCodeGenerator.dll")]
    //[DeploymentItem("OrleansProviders.dll")]
    //[DeploymentItem("TestInternalGrainInterfaces.dll")]
    //[DeploymentItem("TestInternalGrains.dll")]
    public abstract class OrleansTestingBase
    {
        protected static readonly Random random = new Random();

        public Logger logger
        {
            get { return GrainClient.Logger; }
        }

        protected static IGrainFactory GrainFactory { get { return GrainClient.GrainFactory; } }

        protected static long GetRandomGrainId()
        {
            return TestUtils.GetRandomGrainId();
        }

        protected void TestSilosStarted(int expected)
        {
            IManagementGrain mgmtGrain = GrainClient.GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);

            Dictionary<SiloAddress, SiloStatus> statuses = mgmtGrain.GetHosts(onlyActive: true).Result;
            foreach (var pair in statuses)
            {
                logger.Info("       ######## Silo {0}, status: {1}", pair.Key, pair.Value);
                Assert.AreEqual(
                    SiloStatus.Active,
                    pair.Value,
                    "Failed to confirm start of {0} silos ({1} confirmed).",
                    pair.Value,
                    SiloStatus.Active);
            }
            Assert.AreEqual(expected, statuses.Count);
        }
    }
}