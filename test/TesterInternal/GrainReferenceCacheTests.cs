﻿using System;

using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using Xunit;
using UnitTests.Tester;

namespace UnitTests.General
{
    public class GrainReferenceCacheTests : HostedTestClusterEnsureDefaultStarted
    {
        [Fact, TestCategory("Functional"), TestCategory("GetGrain"), TestCategory("Cache")]
        public void GetGrain()
        {
            int size = 1;
            TimeSpan maxAge = TimeSpan.MaxValue;

            var cache = new GrainReferenceCache<int, ISimpleGrain>(size, maxAge,
                GrainCreatorFunc,
                r => r.AsReference<ISimpleGrain>());

            int id = 1;
            var grain = cache.Get(id);

            Assert.Equal(1, cache.Count);
            Assert.Equal(1, numGrainsCreated);

            Assert.NotNull(grain);
            //Assert.Equal(id, grain.A.Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("GetGrain"), TestCategory("Cache")]
        public void GetGrain2()
        {
            int size = 1;
            TimeSpan maxAge = TimeSpan.MaxValue;

            var cache = new GrainReferenceCache<int, ISimpleGrain>(size, maxAge,
                GrainCreatorFunc,
                r => r.AsReference<ISimpleGrain>());

            int id1 = 1;
            var grain1 = cache.Get(id1);

            Assert.NotNull(grain1);
            //Assert.Equal(id1, grain1.A.Result);

            Assert.Equal(1, cache.Count);
            Assert.Equal(1, numGrainsCreated);

            int id2 = 2;
            var grain2 = cache.Get(id2);

            Assert.NotNull(grain2);
            //Assert.Equal(id2, grain2.A.Result);

            Assert.Equal(1, cache.Count);
            Assert.Equal(2, numGrainsCreated);

            //Assert.Equal(id1, grain1.A.Result);
        }

        [Fact, TestCategory("Functional"), TestCategory("GetGrain"), TestCategory("Cache")]
        public void Get2GrainsFromCache()
        {
            int size = 2;
            TimeSpan maxAge = TimeSpan.MaxValue;

            var cache = new GrainReferenceCache<int, ISimpleGrain>(size, maxAge,
                GrainCreatorFunc,
                r => r.AsReference<ISimpleGrain>());

            int id1 = 1;
            var grain1 = cache.Get(id1);

            Assert.NotNull(grain1);
            //Assert.Equal(id1, grain1.A.Result);

            Assert.Equal(1, cache.Count);
            Assert.Equal(1, numGrainsCreated);

            int id2 = 2;
            var grain2 = cache.Get(id2);

            Assert.NotNull(grain2);
            //Assert.Equal(id2, grain2.A.Result);

            Assert.Equal(2, cache.Count);
            Assert.Equal(2, numGrainsCreated);

            //Assert.Equal(id1, grain1.A.Result);
        }

        private int numGrainsCreated;

        private ISimpleGrain GrainCreatorFunc(int key)
        {
            numGrainsCreated++;
            return GrainClient.GrainFactory.GetGrain<ISimpleGrain>(random.Next(), UnitTests.Grains.SimpleGrain.SimpleGrainNamePrefix);
        }
    }
}
