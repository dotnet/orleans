using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using UnitTestGrainInterfaces;

#pragma warning disable 618

namespace UnitTests.General
{
    [TestClass]
    public class SelfManagedTests : UnitTestBase
    {

        public SelfManagedTests()
            : base(true)
        {
        }

        public SelfManagedTests(int dummy)
            : base(new Options { StartPrimary = true, StartSecondary = false, StartClient = true })
        {
        }

        public SelfManagedTests(bool startClientOnly)
            : base(startClientOnly ?
                        new Options { StartPrimary = false, StartSecondary = false, StartClient = true } :
                        new Options { StartFreshOrleans = true })
        {
        }

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void SelfManaged_ActivateAndUpdate()
        {
            ISimpleSelfManagedGrain g1 = SimpleSelfManagedGrainFactory.GetGrain(1);
            ISimpleSelfManagedGrain g2 = SimpleSelfManagedGrainFactory.GetGrain(2);
            Assert.AreEqual(1L, g1.GetPrimaryKeyLong());
            Assert.AreEqual(1L, g1.GetKey().Result);
            Assert.AreEqual("1", g1.GetLabel().Result);
            Assert.AreEqual(2L, g2.GetKey().Result);
            Assert.AreEqual("2", g2.GetLabel().Result);

            g1.SetLabel("one").Wait();
            Assert.AreEqual("one", g1.GetLabel().Result);
            Assert.AreEqual("2", g2.GetLabel().Result);

            ISimpleSelfManagedGrain g1a = SimpleSelfManagedGrainFactory.GetGrain(1);
            Assert.AreEqual("one", g1a.GetLabel().Result);
        }
        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void SelfManaged_Guid_ActivateAndUpdate()
        {
            Guid guid1 = Guid.NewGuid();
            Guid guid2 = Guid.NewGuid();

            IGuidSimpleSelfManagedGrain g1 = GuidSimpleSelfManagedGrainFactory.GetGrain(guid1);
            IGuidSimpleSelfManagedGrain g2 = GuidSimpleSelfManagedGrainFactory.GetGrain(guid2);
            Assert.AreEqual(guid1, g1.GetPrimaryKey());
            Assert.AreEqual(guid1, g1.GetKey().Result);
            Assert.AreEqual(guid1.ToString(), g1.GetLabel().Result);
            Assert.AreEqual(guid2, g2.GetKey().Result);
            Assert.AreEqual(guid2.ToString(), g2.GetLabel().Result);

            g1.SetLabel("one").Wait();
            Assert.AreEqual("one", g1.GetLabel().Result);
            Assert.AreEqual(guid2.ToString(), g2.GetLabel().Result);

            IGuidSimpleSelfManagedGrain g1a = GuidSimpleSelfManagedGrainFactory.GetGrain(guid1);
            Assert.AreEqual("one", g1a.GetLabel().Result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("ActivateDeactivate"), TestCategory("ErrorHandling"), TestCategory("GetGrain")]
        public void SelfManaged_Fail()
        {
            bool failed;
            long key = 0;
            try
            {
                // Key values of -2 are not allowed in this case
                ISimpleSelfManagedGrain fail = SimpleSelfManagedGrainFactory.GetGrain(-2);
                key = fail.GetKey().Result;
                failed = false;
            }
            catch (Exception e)
            {
                Assert.IsInstanceOfType(e.GetBaseException(), typeof(OrleansException));
                failed = true;
            }

            if (!failed) Assert.Fail("Should have failed, but instead returned " + key);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void SelfManaged_ULong_MaxValue()
        {
            ulong key1AsUlong = UInt64.MaxValue; // == -1L
            long key1 = (long)key1AsUlong;

            ISimpleSelfManagedGrain g1 = SimpleSelfManagedGrainFactory.GetGrain(key1);
            Assert.AreEqual(key1, g1.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.AreEqual(key1, g1.GetKey().Result);
            Assert.AreEqual(key1.ToString(CultureInfo.InvariantCulture), g1.GetLabel().Result);

            g1.SetLabel("MaxValue").Wait();
            Assert.AreEqual("MaxValue", g1.GetLabel().Result);

            ISimpleSelfManagedGrain g1a = SimpleSelfManagedGrainFactory.GetGrain((long)key1AsUlong);
            Assert.AreEqual("MaxValue", g1a.GetLabel().Result);
            Assert.AreEqual(key1, g1a.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1a.GetKey().Result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void SelfManaged_ULong_MinValue()
        {
            ulong key1AsUlong = UInt64.MinValue; // == zero
            long key1 = (long)key1AsUlong;

            ISimpleSelfManagedGrain g1 = SimpleSelfManagedGrainFactory.GetGrain(key1);
            Assert.AreEqual(key1, g1.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.AreEqual(key1, g1.GetPrimaryKeyLong());
            Assert.AreEqual(key1, g1.GetKey().Result);
            Assert.AreEqual(key1.ToString(CultureInfo.InvariantCulture), g1.GetLabel().Result);

            g1.SetLabel("MinValue").Wait();
            Assert.AreEqual("MinValue", g1.GetLabel().Result);

            ISimpleSelfManagedGrain g1a = SimpleSelfManagedGrainFactory.GetGrain((long)key1AsUlong);
            Assert.AreEqual("MinValue", g1a.GetLabel().Result);
            Assert.AreEqual(key1, g1a.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1a.GetKey().Result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void SelfManaged_Long_MaxValue()
        {
            long key1 = Int32.MaxValue;
            ulong key1AsUlong = (ulong)key1;

            ISimpleSelfManagedGrain g1 = SimpleSelfManagedGrainFactory.GetGrain(key1);
            Assert.AreEqual(key1, g1.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.AreEqual(key1, g1.GetKey().Result);
            Assert.AreEqual(key1.ToString(CultureInfo.InvariantCulture), g1.GetLabel().Result);

            g1.SetLabel("MaxValue").Wait();
            Assert.AreEqual("MaxValue", g1.GetLabel().Result);

            ISimpleSelfManagedGrain g1a = SimpleSelfManagedGrainFactory.GetGrain((long)key1AsUlong);
            Assert.AreEqual("MaxValue", g1a.GetLabel().Result);
            Assert.AreEqual(key1, g1a.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1a.GetKey().Result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public void SelfManaged_Long_MinValue()
        {
            long key1 = Int64.MinValue;
            ulong key1AsUlong = (ulong)key1;

            ISimpleSelfManagedGrain g1 = SimpleSelfManagedGrainFactory.GetGrain(key1);
            Assert.AreEqual((long)key1AsUlong, g1.GetPrimaryKeyLong());
            Assert.AreEqual(key1, g1.GetPrimaryKeyLong());
            Assert.AreEqual(key1, g1.GetKey().Result);
            Assert.AreEqual(key1.ToString(CultureInfo.InvariantCulture), g1.GetLabel().Result);

            g1.SetLabel("MinValue").Wait();
            Assert.AreEqual("MinValue", g1.GetLabel().Result);

            ISimpleSelfManagedGrain g1a = SimpleSelfManagedGrainFactory.GetGrain((long)key1AsUlong);
            Assert.AreEqual("MinValue", g1a.GetLabel().Result);
            Assert.AreEqual(key1, g1a.GetPrimaryKeyLong());
            Assert.AreEqual((long)key1AsUlong, g1a.GetKey().Result);
        }

        //[TestMethod, TestCategory("Revisit"), TestCategory("ActivateDeactivate")]
        //public void SelfManaged_Placement()
        //{
        //    //IProxyGrain proxy0 = ProxyGrainFactory.CreateGrain(new[] { GrainStrategy.PartitionPlacement(0) });
        //    //IProxyGrain proxy1 = ProxyGrainFactory.CreateGrain(new[] { GrainStrategy.PartitionPlacement(1) });

        //    //proxy0.CreateProxy(1).Wait(;)
        //    //proxy1.CreateProxy(1).Wait();

        //    //var silo0 = proxy0.GetRuntimeInstanceId().Result;
        //    //var proxySilo0 = proxy0.GetProxyRuntimeInstanceId().Result;
        //    //var silo1 = proxy1.GetRuntimeInstanceId().Result;
        //    //var proxySilo1 = proxy1.GetProxyRuntimeInstanceId().Result;

        //    //if (silo0.Equals(silo1))
        //    //    Assert.Inconclusive("Only one active silo, cannot test placement");

        //    //Assert.AreEqual(silo0, proxySilo0, "Self-managed grain should be created on local silo");
        //    //Assert.AreEqual(silo1, proxySilo1, "Self-managed grain should be created on local silo");
        //}

        //[TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("General")]
        //public void SelfManaged_Observer()
        //{
        //    var simple = SimpleSelfManagedGrainFactory.GetGrain(3);
        //    simple.SetLabel("one").Wait();
        //    var stream = Stream_OLDFactory.Cast(simple);
        //    stream.Wait();
        //    stream.Next("+two");
        //    Thread.Sleep(100); // wait for stream message to propagate
        //    var label = simple.GetLabel().Result;
        //    Assert.AreEqual("one+two", label, "Stream message should have arrived");
        //}

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("ActivateDeactivate")]
        public void SelfManaged_MultipleGrainInterfaces()
        {
            ISimpleSelfManagedGrain simple = SimpleSelfManagedGrainFactory.GetGrain(50);

            simple.GetMultipleGrainInterfaces_List().Wait();
            logger.Info("GetMultipleGrainInterfaces_List() worked");

            simple.GetMultipleGrainInterfaces_Array().Wait();

            logger.Info("GetMultipleGrainInterfaces_Array() worked");
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("ActivateDeactivate"), TestCategory("Reentrancy")]
        public void SelfManaged_Reentrant_RecoveryAfterExpiredMessage()
        {
            List<Task> promises = new List<Task>();
            TimeSpan prevTimeout = RuntimeClient.Current.GetResponseTimeout();

            // set short response time and ask to do long operation, to trigger expired msgs in the silo queues.
            TimeSpan shortTimeout = TimeSpan.FromMilliseconds(1000);
            RuntimeClient.Current.SetResponseTimeout(shortTimeout);

            ISimpleSelfManagedGrain grain = SimpleSelfManagedGrainFactory.GetGrain(12);
            int num = 10;
            for (long i = 0; i < num; i++)
            {
                Task task = grain.DoLongAction(shortTimeout.Multiply(3), "A_" + i);
                promises.Add(task);
            }
            try
            {
                Task.WhenAll(promises).Wait();
            }catch(Exception)
            {
                logger.Info("Done with stress iteration.");
            }

            // wait a bit to make sure expired msgs in the silo is trigered.
            Thread.Sleep(TimeSpan.FromSeconds(10));

            // set the regular response time back, expect msgs ot succeed.
            RuntimeClient.Current.SetResponseTimeout(prevTimeout);

            logger.Info("About to send a next legit request that should succeed.");
            grain.DoLongAction(TimeSpan.FromMilliseconds(1), "B_" + 0).Wait();
            logger.Info("The request succeeded.");
        }

        [TestMethod, TestCategory("RequestContext"), TestCategory("GetGrain")]
        public void SelfManaged_TestRequestContext()
        {
            ISimpleSelfManagedGrain g1 = SimpleSelfManagedGrainFactory.GetGrain(1);
            Task<Tuple<string, string>> promise1 = g1.TestRequestContext();
            Tuple<string, string> requstContext = promise1.Result;
            logger.Info("Request Context is: " + requstContext);
            Assert.IsNotNull(requstContext.Item2, "Item2=" + requstContext.Item2);
            Assert.IsNotNull(requstContext.Item1, "Item1=" + requstContext.Item1);
        }
    }
}

#pragma warning restore 618
