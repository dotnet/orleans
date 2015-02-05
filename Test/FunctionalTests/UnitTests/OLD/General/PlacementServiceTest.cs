using Orleans.PlacementService;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using Orleans.RuntimeCore;
using System.Collections.Generic;
using System.Net;
using Orleans.Counters;

namespace UnitTests
{
    /// <summary>
    ///This is a test class for PlacementServiceTest and is intended
    ///to contain all PlacementServiceTest Unit Tests
    ///</summary>
    [TestClass()]
    public class PlacementServiceTest
    {

        private TestContext testContextInstance;

        /// <summary>
        ///Gets or sets the test context which provides
        ///information about and functionality for the current test run.
        ///</summary>
        public TestContext TestContext
        {
            get
            {
                return testContextInstance;
            }
            set
            {
                testContextInstance = value;
            }
        }

        #region Additional test attributes
        // 
        //You can use the following additional attributes as you write your tests:
        //
        //Use ClassInitialize to run code before running the first test in the class
        //[ClassInitialize()]
        //public static void MyClassInitialize(TestContext testContext)
        //{
        //}
        //
        //Use ClassCleanup to run code after all tests in a class have run
        //[ClassCleanup()]
        //public static void MyClassCleanup()
        //{
        //}
        //
        //Use TestInitialize to run code before running each test
        //[TestInitialize()]
        //public void MyTestInitialize()
        //{
        //}
        //
        //Use TestCleanup to run code after each test has run
        //[TestCleanup()]
        //public void MyTestCleanup()
        //{
        //}
        //
        #endregion


        private class PerfMetrics : ISiloPerformanceMetrics
        {
            #region IPerformanceMetrics Members

            public long RequestQueueLength
            {
                get;
                set;
            }

            public int ActivationCount
            {
                get;
                set;
            }

            #endregion


            public int SendQueueLength
            {
                get { throw new NotImplementedException(); }
            }

            public int ReceiveQueueLength
            {
                get { throw new NotImplementedException(); }
            }

            public float CpuUsage
            {
                get { throw new NotImplementedException(); }
            }

            public long MemoryUsage
            {
                get { throw new NotImplementedException(); }
            }

            public long ClientCount
            {
                get { throw new NotImplementedException(); }
            }

            public long SentMessages
            {
                get { throw new NotImplementedException(); }
            }

            public long ReceivedMessages
            {
                get { throw new NotImplementedException(); }
            }

            //public TimeSpan ReportPeriod
            //{
            //    get
            //    {
            //        throw new NotImplementedException();
            //    }
            //    set
            //    {
            //        throw new NotImplementedException();
            //    }
            //}

            public bool IsOverloaded
            {
                get { throw new NotImplementedException(); }
            }

            public void LatchIsOverload(bool overloaded)
            {
                throw new NotImplementedException();
            }

            public void UnlatchIsOverloaded()
            {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        ///A test for PlacementService Constructor
        ///</summary>
        //[TestMethod()]
        public void Placement_ConstructorTest()
        {
            PerfMetrics pm = new PerfMetrics();
            NodeConfiguration config = new NodeConfiguration();
            PlacementService target = new PlacementService(pm, config);
            Assert.AreEqual<long>(config.MaxRequestQueueLength, target.MaxRequestQueueLength, "MaxRequestQueueLength initialized incorrectly");
            Assert.AreEqual<double>(config.NewActivationRate, target.NewActivationRate, "NewActivationRate initialized incorrectly");
            Assert.AreEqual<double>(config.LocalPlacementRate, target.LocalPlacementRate, "LocalPlacementRate initialized incorrectly");
        }

        /// <summary>
        ///A test for PlaceNewActivation
        ///</summary>
        //[TestMethod()]
        public void Placement_PlaceNewActivationTest()
        {
            PerfMetrics pm = new PerfMetrics();
            NodeConfiguration config = new NodeConfiguration();
            PlacementService target = new PlacementService(pm, config);
            SiloAddress here = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 25));
            List<SiloAddress> allNodes = new List<SiloAddress>();
            for (int i = 25; i < 30; i++)
            {
                allNodes.Add(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, i)));
            }

            // Test with LocalPlacementRate of 100%
            target.LocalPlacementRate = 1.0;
            SiloAddress expected = here;
            SiloAddress actual;
            actual = target.PlaceNewActivation(here, allNodes);
            Assert.IsTrue(expected.Equals(actual), "Local silo not returned when local placement rate is 100%");

            // Now try a statistical test -- commented out because it failed too sporadically
            //target.LocalPlacementRate = 0.50;
            //Dictionary<SiloAddress, int> counters = new Dictionary<SiloAddress, int>();
            //foreach (SiloAddress addr in allNodes)
            //{
            //    counters[addr] = 0;
            //}
            //// Run 1,000 placements
            //for (int i = 0; i < 1000; i++)
            //{
            //    counters[target.PlaceNewActivation(here, allNodes)]++;
            //}
            //foreach (SiloAddress addr in allNodes)
            //{
            //    int count = counters[addr];
            //    int expectedCount = 100;
            //    if (addr.Equals(here))
            //    {
            //        expectedCount = 600; // 50% plus 1/5th of the rest
            //    }
            //    int low = (expectedCount * 4) / 5;
            //    int high = (expectedCount * 6) / 5;
            //    Assert.IsTrue((low <= count) && (count <= high), "Count is out of bounds: actual is " + count + ", expected was " + expectedCount);
            //}
        }

        /// <summary>
        ///A test for SelectSilo
        ///</summary>
        //[TestMethod()]
        public void Placement_SelectSiloTest()
        {
            ISiloPerformanceMetrics pm = null; // TODO: Initialize to an appropriate value
            NodeConfiguration config = new NodeConfiguration();
            PlacementService target = new PlacementService(pm, config); // TODO: Initialize to an appropriate value
            List<SiloAddress> nodes = null; // TODO: Initialize to an appropriate value
            SiloAddress expected = null; // TODO: Initialize to an appropriate value
            SiloAddress actual;
            actual = target.SelectSilo(nodes);
            Assert.AreEqual(expected, actual);
            Assert.Inconclusive("Verify the correctness of this test method.");
        }

        /// <summary>
        ///A test for ShouldCreateNewActivation
        ///</summary>
        //[TestMethod()]
        public void Placement_ShouldCreateNewActivationTest()
        {
            ISiloPerformanceMetrics pm = null; // TODO: Initialize to an appropriate value
            NodeConfiguration config = new NodeConfiguration();
            PlacementService target = new PlacementService(pm, config); // TODO: Initialize to an appropriate value
            GrainId grain = null; // TODO: Initialize to an appropriate value
            int currentCount = 0; // TODO: Initialize to an appropriate value
            bool expected = false; // TODO: Initialize to an appropriate value
            bool actual;
            actual = target.ShouldCreateNewActivation(grain, currentCount);
            Assert.AreEqual(expected, actual);
            Assert.Inconclusive("Verify the correctness of this test method.");
        }

        /// <summary>
        ///A test for UseLocalSilo
        ///</summary>
        //[TestMethod()]
        public void Placement_UseLocalSiloTest()
        {
            ISiloPerformanceMetrics pm = null; // TODO: Initialize to an appropriate value
            NodeConfiguration config = new NodeConfiguration();
            PlacementService target = new PlacementService(pm, config); // TODO: Initialize to an appropriate value
            GrainId grain = null; // TODO: Initialize to an appropriate value
            bool expected = false; // TODO: Initialize to an appropriate value
            bool actual;
            actual = target.UseLocalSilo(grain);
            Assert.AreEqual(expected, actual);
            Assert.Inconclusive("Verify the correctness of this test method.");
        }
    }
}
