using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using UnitTestGrains;

namespace UnitTests.ConcurrencyTests
{
    /// <summary>
    /// Summary description for PersistenceTest
    /// </summary>
    [TestClass]
    public class ConcurrencyTests : UnitTestBase
    {
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(10);

        public ConcurrencyTests()
            : base(new Options { StartSecondary = false, MaxActiveThreads = 2 })
        {
            Console.WriteLine("#### ConcurrencyTests() is called.");
        }

        [TestCleanup]
        public void Cleanup()
        {
            ResetAllAdditionalRuntimes();
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("ReadOnly"), TestCategory("AsynchronyPrimitives")]
        public async Task ConcurrencyTest_ReadOnly()
        {
            IConcurrentGrain first = ConcurrentGrainFactory.GetGrain(GetRandomGrainId());
            first.Initialize(0).Wait();

            List<Task> promises = new List<Task>();
            for (int i = 0; i < 5; i++)
            {
                Task p = first.A();
                promises.Add(p);
            }
            await Task.WhenAll(promises);
            Console.WriteLine("\n\nENDED TEST\n\n");
        }

        [TestMethod, TestCategory("Nightly"), TestCategory("ReadOnly"), TestCategory("AsynchronyPrimitives")]
        public void ConcurrencyTest_ModifyReturnList()
        {
            IConcurrentGrain grain = ConcurrentGrainFactory.GetGrain(GetRandomGrainId());
            
            Console.WriteLine("\n\nStarting TEST\n\n");

            Task<List<int>>[] ll = new Task<List<int>>[20];
            for (int i = 0; i < 2000; i++)
            {
                for (int j = 0; j < ll.Length; j++)
                    ll[j] = grain.ModifyReturnList_Test();

                Task.WhenAll(ll).Wait();
                Console.Write(".");
            }
            Console.WriteLine("\n\nENDED TEST\n\n");
        }

        // [TestCategory("BVT"), TestCategory("Nightly")]
        [TestMethod, TestCategory("AsynchronyPrimitives")]
        public async Task ConcurrencyTest_TailCall_1()
        {
            IConcurrentGrain grain1 = ConcurrentGrainFactory.GetGrain(GetRandomGrainId());
            IConcurrentReentrantGrain grain2 = ConcurrentReentrantGrainFactory.GetGrain(GetRandomGrainId());
            grain1.Initialize_2(1).Wait();
            grain2.Initialize_2(2).Wait();

            Console.WriteLine("\n\nStarting TEST\n\n");

            Task<int> retVal1 = grain1.TailCall_Caller(grain2, false);
            Task<int> retVal2 = grain1.TailCall_Resolver(grain2);

            await Task.WhenAll(retVal1, retVal2);
            
            Assert.AreEqual(7, retVal1.Result);
            Assert.AreEqual(8, retVal2.Result);
            Console.Write(".");
            
            Console.WriteLine("\n\nENDED TEST\n\n");
        }
    }
}
