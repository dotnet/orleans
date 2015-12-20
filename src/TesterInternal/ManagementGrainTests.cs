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
    public class ManagementGrainTests : UnitTestSiloHost
    {
        private IManagementGrain mgmtGrain;

        [TestInitialize]
        public void TestInitialize()
        {
            mgmtGrain = GrainClient.GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Management")]
        public void GetHosts()
        {
            Dictionary<SiloAddress, SiloStatus> siloStatuses = mgmtGrain.GetHosts(true).Result;
            Assert.IsNotNull(siloStatuses, "Got some silo statuses");
            Assert.AreEqual(2, siloStatuses.Count, "Number of silo statuses");
        }

        private SimpleGrainStatistic[] GetSimpleGrainStatistics(string when)
        {
            SimpleGrainStatistic[] stats = mgmtGrain.GetSimpleGrainStatistics(null).Result;
            StringBuilder sb = new StringBuilder();
            foreach (var s in stats) sb.Append(s).AppendLine();
            Console.WriteLine("Grain statistics returned by Orleans Management Grain - " + when + " : " + sb);
            return stats;
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
        public void GetGrainStatistics_ActivationCounts_OrleansManagedGrains()
        {
            SimpleGrainStatistic[] stats = GetSimpleGrainStatistics("Before Create");
            Assert.IsTrue(stats.Length > 0, "Got some grain statistics: " + stats.Length);

            string grainType = typeof(SimpleGrain).FullName;
            Assert.AreEqual(0, stats.Count(s => s.GrainType == grainType), "No activation counter yet for grain: " + grainType);
            ISimpleGrain grain1 = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix);
            int x = grain1.GetA().Result; // Call grain method
            stats = GetSimpleGrainStatistics("After Invoke");
            Assert.AreEqual(1, stats.Count(s => s.GrainType == grainType), "Activation counter now exists for grain: " + grainType);
            SimpleGrainStatistic grainStats = stats.First(s => s.GrainType == grainType);
            Assert.AreEqual(1, grainStats.ActivationCount, "Activation count for grain after activation: " + grainType);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Management")]
        public void GetGrainStatistics_ActivationCounts_SelfManagedGrains()
        {
            SimpleGrainStatistic[] stats = GetSimpleGrainStatistics("Before Create");
            Assert.IsTrue(stats.Length > 0, "Got some grain statistics: " + stats.Length);

            string grainType = typeof(TestGrain).FullName;
            Assert.AreEqual(0, stats.Count(s => s.GrainType == grainType), "No activation counter yet for grain: " + grainType);
            ITestGrain grain1 = GrainClient.GrainFactory.GetGrain<ITestGrain>(1);
            long x = grain1.GetKey().Result; // Call grain method
            stats = GetSimpleGrainStatistics("After Invoke");
            Assert.AreEqual(1, stats.Count(s => s.GrainType == grainType), "Activation counter now exists for grain: " + grainType);
            SimpleGrainStatistic grainStats = stats.First(s => s.GrainType == grainType);
            Assert.AreEqual(1, grainStats.ActivationCount, "Activation count for grain after activation: " + grainType);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Management")]
        public void GetSimpleGrainStatistics_ActivationCounts_OrleansManagedGrains()
        {
            SimpleGrainStatistic[] stats = GetSimpleGrainStatistics("Before Create");
            Assert.IsTrue(stats.Length > 0, "Got some grain statistics: " + stats.Length);

            string grainType = typeof(SimpleGrain).FullName;
            Assert.AreEqual(0, stats.Count(s => s.GrainType == grainType), "No activation counter yet for grain: " + grainType);
            ISimpleGrain grain1 = GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), SimpleGrain.SimpleGrainNamePrefix);
            grain1.GetA().Wait(); // Call grain method
            stats = GetSimpleGrainStatistics("After Invoke");
            Assert.AreEqual(1, stats.Count(s => s.GrainType == grainType), "Activation counter now exists for grain: " + grainType);
            SimpleGrainStatistic grainStats = stats.First(s => s.GrainType == grainType);
            Assert.AreEqual(1, grainStats.ActivationCount, "Activation count for grain after activation: " + grainType);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Management")]
        public void GetSimpleGrainStatistics_ActivationCounts_SelfManagedGrains()
        {
            SimpleGrainStatistic[] stats = GetSimpleGrainStatistics("Before Create");
            Assert.IsTrue(stats.Length > 0, "Got some grain statistics: " + stats.Length);

            string grainType = typeof(TestGrain).FullName;
            Assert.AreEqual(0, stats.Count(s => s.GrainType == grainType), "No activation counter yet for grain: " + grainType);
            ITestGrain grain1 = GrainClient.GrainFactory.GetGrain<ITestGrain>(2);
            grain1.GetKey().Wait(); // Call grain method
            stats = GetSimpleGrainStatistics("After Invoke");
            Assert.AreEqual(1, stats.Count(s => s.GrainType == grainType), "Activation counter now exists for grain: " + grainType);
            SimpleGrainStatistic grainStats = stats.First(s => s.GrainType == grainType);
            Assert.AreEqual(1, grainStats.ActivationCount, "Activation count for grain after activation: " + grainType);
        }
    }
}

// ReSharper restore ConvertToConstant.Local
