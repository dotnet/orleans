using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Samples.Testing;

using Orleans.Samples.Testing.UnitTests.GrainInterfaces;

namespace Orleans.Samples.Testing.UnitTests
{
    /// <summary>
    /// Some example test cases using the UnitTestBase host class.
    /// </summary>
    [TestClass]
    public class UnitTests
    {
        private static readonly UnitTestSiloOptions siloOptions = new UnitTestSiloOptions
        {
            StartFreshOrleans = true
        };
        private static readonly UnitTestClientOptions clientOptions = new UnitTestClientOptions
        {
            ResponseTimeout = TimeSpan.FromSeconds(30)
        };

        private static UnitTestSiloHost unitTestSiloHost;

        private static readonly Random random = new Random();

        [ClassInitialize]
        public static void ClassInitialize(TestContext testContext)
        {
            unitTestSiloHost = new UnitTestSiloHost(siloOptions, clientOptions);
            
            Assert.AreEqual(2, unitTestSiloHost.GetActiveSilos().Count(), 
                "Silo count at start of tests");
        }
        [ClassCleanup]
        public static void ClassCleanup()
        {
            if (unitTestSiloHost != null)
            {
                try
                {
                    unitTestSiloHost.StopDefaultSilos();
                } 
                catch (Exception exc) { Console.WriteLine(exc); }
            }
            unitTestSiloHost = null;
        }

        [TestMethod, TestCategory("ExampleUnitTest")]
        public async Task Ping()
        {
            long id = random.Next();
            var grain = TestGrainFactory.GetGrain(id);
            string response = await grain.Test("ping");
            Assert.AreEqual("ACK:ping", response);
        }

        [TestMethod, TestCategory("ExampleUnitTest")]
        public async Task SetGetValue()
        {
            long id = random.Next();
            var grain = TestGrainFactory.GetGrain(id);
            await grain.SetValue(5);
            int result = await grain.GetValue();
            Assert.AreEqual(5, result);
        }

        [TestMethod, TestCategory("ExampleUnitTest")]
        public async Task SetGetValue_RestartSilos()
        {
            long id = random.Next();
            var grain = TestGrainFactory.GetGrain(id);

            await grain.SetValue(10);
            
            unitTestSiloHost.RestartDefaultSilos();

            int result = await grain.GetValue();
            // Note: Grain is re-initialized to default state when persistence is not used
            //       so expected value is zero here.
            Assert.AreEqual(0, result);
            // Note: If grain was using external persistent storage such as Azure Table, 
            //       then the original value would survive across silo restarts.
            //Assert.AreEqual(10, result);
        }
    }
}
