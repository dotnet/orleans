﻿using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace UnitTests.ConcurrencyTests
{
    /// <summary>
    /// Summary description for PersistenceTest
    /// </summary>
    public class ConcurrencyTests : OrleansTestingBase, IClassFixture<ConcurrencyTests.Fixture>
    {
        private class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions();
                options.ClusterConfiguration.ApplyToAllNodes(n => n.MaxActiveThreads = 2);

                return new TestCluster(options);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("ReadOnly"), TestCategory("AsynchronyPrimitives")]
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
        }

        [Fact, TestCategory("Functional"), TestCategory("ReadOnly"), TestCategory("AsynchronyPrimitives")]
        public void ConcurrencyTest_ModifyReturnList()
        {
            IConcurrentGrain grain = GrainClient.GrainFactory.GetGrain<IConcurrentGrain>(GetRandomGrainId());

            Task<List<int>>[] ll = new Task<List<int>>[20];
            for (int i = 0; i < 2000; i++)
            {
                for (int j = 0; j < ll.Length; j++)
                    ll[j] = grain.ModifyReturnList_Test();

                Task.WhenAll(ll).Wait();
            }
        }
    }
}
