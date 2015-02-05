#if REVISIT_THESE_TESTS

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Orleans;
using UnitTestGrainInterfaces;

namespace UnitTests
{
    /// <summary>
    /// </summary>
    [TestClass]
    public class Liveness_Set_1_RegularGrain : UnitTestBase
    {
        public TestContext TestContext { get; set; }

        public Liveness_Set_1_RegularGrain()
            : base(true)
        {
            Console.WriteLine("#### Liveness_Set_1_RegularGrain.");
        }

        [TestCleanup]
        public void TestCleanup()
        {
            Console.WriteLine("Test {0} completed - Outcome = {1}", TestContext.TestName, TestContext.CurrentTestOutcome);
            ResetAllAdditionalRuntimes();
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("Revisit"), TestCategory("Liveness")]
        public void Liveness_Set_1_1()
        {
            Liveness_GenericStopTest(false, false);
        }

        [TestMethod, TestCategory("Revisit"), TestCategory("Liveness")]
        public void Liveness_Set_1_2()
        {
            Liveness_GenericStopTest(true, false);
        }
        [TestMethod, TestCategory("Revisit"), TestCategory("Liveness")]
        public void Liveness_Set_1_3()
        {
            Liveness_GenericStopTest(false, true);
        }
        [TestMethod, TestCategory("Revisit"), TestCategory("Liveness")]
        public void Liveness_Set_1_4()
        {
            Liveness_GenericStopTest(true, true);
        }

        [TestMethod, TestCategory("Revisit")]
        public void Liveness_Set_1_5()
        {
            //WaitForLivenessToStabilize();

            //var silo0 = new[] { GrainStrategy.PartitionPlacement(0) };
            //var silo1 = new[] { GrainStrategy.PartitionPlacement(1) };

            //IReliabilityTestGrain a = ReliabilityTestGrainFactory.CreateGrain(Label: "A", Strategies: silo0);
            //IReliabilityTestGrain b = ReliabilityTestGrainFactory.CreateGrain(Label: "B", Strategies: silo1);
            //IReliabilityTestGrain c = ReliabilityTestGrainFactory.CreateGrain(Label: "C", Strategies: silo1);

            //a.Wait();
            //b.Wait();
            //c.Wait();

            //ResetRuntime(Secondary);

            //string la = null;
            //string lb = null;
            //string lc = null;

            //la = a.Label.Result;
            //Assert.AreEqual("A", la);

            //try
            //{
            //    lb = b.Label.Result;
            //}
            //catch (Exception exc)
            //{
            //    Exception baseExc = exc.GetBaseException();
            //    lb = baseExc.ToString();
            //    Assert.IsTrue(baseExc is OrleansException || baseExc is TimeoutException);
            //    lb = "failed";
            //}
            //try
            //{
            //    lc = c.Label.Result;
            //}
            //catch (Exception exc)
            //{
            //    Exception baseExc = exc.GetBaseException();
            //    lc = baseExc.ToString();
            //    Assert.IsTrue(baseExc is OrleansException || baseExc is TimeoutException);
            //    lc = "failed";
            //}

            ////Assert.AreEqual("B,C", lb + "," + lc);
            //Assert.AreEqual("failed,failed", lb + "," + lc);
        }
    }
}
#endif