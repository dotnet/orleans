using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.TestingHost;
using TestInternalGrainInterfaces;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.ConcurrencyTests
{
    /// <summary>
    /// Summary description for PersistenceTest
    /// </summary>
    [TestClass]
    public class ConcurrencyTests : UnitTestSiloHost
    {
        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(10);

        public ConcurrencyTests()
            : base(new TestingSiloOptions
            {
                StartSecondary = false,
                AdjustConfig = config=>{
                    config.Overrides["Primary"].MaxActiveThreads = 2;
                },
            })
        {
            Console.WriteLine("#### ConcurrencyTests() is called.");
        }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ReadOnly"), TestCategory("AsynchronyPrimitives")]
        public async Task ConcurrencyTest_ReadOnly()
        {
            IConcurrentGrain first = GrainClient.GrainFactory.GetGrain<IConcurrentGrain>(GetRandomGrainId());
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

        [TestMethod, TestCategory("Functional"), TestCategory("ReadOnly"), TestCategory("AsynchronyPrimitives")]
        public void ConcurrencyTest_ModifyReturnList()
        {
            IConcurrentGrain grain = GrainClient.GrainFactory.GetGrain<IConcurrentGrain>(GetRandomGrainId());

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
    }
}
