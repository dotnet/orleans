using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;


namespace UnitTests.General
{
    [TestClass]
    public class GrainReferenceCacheTests : UnitTestBase
    {
        public GrainReferenceCacheTests()
            : base(false)
        {
        }

        [ClassCleanup()]
        public static void MyClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("GetGrain"), TestCategory("Cache")]
        public void GetGrain()
        {
            int size = 1;
            TimeSpan maxAge = TimeSpan.MaxValue;

            var cache = new GrainReferenceCache<int, ISimpleGrain>(size, maxAge,
                GrainCreatorFunc,
                SimpleGrainFactory.Cast);

            int id = 1;
            var grain = cache.Get(id);

            Assert.AreEqual(1, cache.Count);
            Assert.AreEqual(1, numGrainsCreated);

            Assert.IsNotNull(grain);
            //Assert.AreEqual(id, grain.A.Result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("GetGrain"), TestCategory("Cache")]
        public void GetGrain2()
        {
            int size = 1;
            TimeSpan maxAge = TimeSpan.MaxValue;

            var cache = new GrainReferenceCache<int, ISimpleGrain>(size, maxAge,
                GrainCreatorFunc,
                SimpleGrainFactory.Cast);

            int id1 = 1;
            var grain1 = cache.Get(id1);

            Assert.IsNotNull(grain1);
            //Assert.AreEqual(id1, grain1.A.Result);

            Assert.AreEqual(1, cache.Count);
            Assert.AreEqual(1, numGrainsCreated);

            int id2 = 2;
            var grain2 = cache.Get(id2);

            Assert.IsNotNull(grain2);
            //Assert.AreEqual(id2, grain2.A.Result);

            Assert.AreEqual(1, cache.Count);
            Assert.AreEqual(2, numGrainsCreated);

            //Assert.AreEqual(id1, grain1.A.Result);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly"), TestCategory("GetGrain"), TestCategory("Cache")]
        public void Get2GrainsFromCache()
        {
            int size = 2;
            TimeSpan maxAge = TimeSpan.MaxValue;

            var cache = new GrainReferenceCache<int, ISimpleGrain>(size, maxAge,
                GrainCreatorFunc,
                SimpleGrainFactory.Cast);

            int id1 = 1;
            var grain1 = cache.Get(id1);

            Assert.IsNotNull(grain1);
            //Assert.AreEqual(id1, grain1.A.Result);

            Assert.AreEqual(1, cache.Count);
            Assert.AreEqual(1, numGrainsCreated);

            int id2 = 2;
            var grain2 = cache.Get(id2);

            Assert.IsNotNull(grain2);
            //Assert.AreEqual(id2, grain2.A.Result);

            Assert.AreEqual(2, cache.Count);
            Assert.AreEqual(2, numGrainsCreated);

            //Assert.AreEqual(id1, grain1.A.Result);
        }

        private int numGrainsCreated;

        private ISimpleGrain GrainCreatorFunc(int key)
        {
            numGrainsCreated++;
            return TestConstants.GetSimpleGrain();
        }
    }
}
