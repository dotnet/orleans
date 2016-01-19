using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using UnitTests.Tester;

// ReSharper disable ConvertToConstant.Local

namespace UnitTests.Management
{
    [TestClass]
    public class ManagementGrainTests : HostedTestClusterEnsureDefaultStarted
    {
        private IManagementGrain mgmtGrain;

        [TestInitialize]
        public void TestInitialize()
        {
            mgmtGrain = GrainClient.GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Management")]
        public void GetHosts()
        {
            Dictionary<SiloAddress, SiloStatus> siloStatuses = mgmtGrain.GetHosts(true).Result;
            Assert.IsNotNull(siloStatuses, "Got some silo statuses");
            Assert.AreEqual(2, siloStatuses.Count, "Number of silo statuses");
        }


        [TestMethod, TestCategory("Functional"), TestCategory("Management")]
        public void GetSimpleGrainStatistics()
        {
            SimpleGrainStatistic[] stats = GetSimpleGrainStatistics("Initial");
            Assert.IsTrue(stats.Length > 0, "Got some grain statistics: " + stats.Length);
            foreach (var s in stats)
            {
                Assert.IsFalse(s.GrainType.EndsWith("Activation"), "Grain type name should not end with 'Activation' - " + s.GrainType);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Management")]
        public void GetSimpleGrainStatistics_ActivationCounts()
        {
            RunGetStatisticsTest<ISimpleGrain, SimpleGrain>(g => g.GetA().Wait());
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Management")]
        public void GetTestGrainStatistics_ActivationCounts()
        {
            RunGetStatisticsTest<ITestGrain, TestGrain>(g => g.GetKey().Wait());
        }

        private void RunGetStatisticsTest<TGrainInterface, TGrain>(Action<TGrainInterface> callGrainMethodAction)
            where TGrainInterface : IGrainWithIntegerKey
            where TGrain : TGrainInterface
        {
            SimpleGrainStatistic[] stats = GetSimpleGrainStatistics("Before Create");
            Assert.IsTrue(stats.Length > 0, "Got some grain statistics: " + stats.Length);

            string grainType = typeof(TGrain).FullName;
            int initialStatisticsCount = stats.Count(s => s.GrainType == grainType);
            int initialActivationsCount = stats.Where(s => s.GrainType == grainType).Sum(s => s.ActivationCount);
            var grain1 = GrainClient.GrainFactory.GetGrain<TGrainInterface>(random.Next());
            callGrainMethodAction(grain1); // Call grain method
            stats = GetSimpleGrainStatistics("After Invoke");
            Assert.IsTrue(stats.Count(s => s.GrainType == grainType) >= initialStatisticsCount, "Activation counter now exists for grain: " + grainType);
            int expectedActivationsCount = initialActivationsCount + 1;
            int actualActivationsCount = stats.Where(s => s.GrainType == grainType).Sum(s => s.ActivationCount);
            Assert.AreEqual(expectedActivationsCount, actualActivationsCount, "Activation count for grain after activation: " + grainType);
        }

        private SimpleGrainStatistic[] GetSimpleGrainStatistics(string when)
        {
            SimpleGrainStatistic[] stats = mgmtGrain.GetSimpleGrainStatistics(null).Result;
            StringBuilder sb = new StringBuilder();
            foreach (var s in stats) sb.AppendLine().Append(s);
            sb.AppendLine();
            Console.WriteLine("Grain statistics returned by Orleans Management Grain - " + when + " : " + sb);
            return stats;
        }
    }
}

// ReSharper restore ConvertToConstant.Local
