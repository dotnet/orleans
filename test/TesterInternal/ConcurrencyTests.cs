using System.Collections.Generic;
using System.Threading.Tasks;
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
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
        }

        public ConcurrencyTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact, TestCategory("Functional"), TestCategory("ReadOnly"), TestCategory("AsynchronyPrimitives")]
        public async Task ConcurrencyTest_ReadOnly()
        {
            var first = this.fixture.GrainFactory.GetGrain<IConcurrentGrain>(GetRandomGrainId());
            first.Initialize(0).Wait();

            var promises = new List<Task>();
            for (var i = 0; i < 5; i++)
            {
                Task p = first.A();
                promises.Add(p);
            }
            await Task.WhenAll(promises);
        }

        [Fact, TestCategory("Functional"), TestCategory("ReadOnly"), TestCategory("AsynchronyPrimitives")]
        public void ConcurrencyTest_ModifyReturnList()
        {
            var grain = this.fixture.GrainFactory.GetGrain<IConcurrentGrain>(GetRandomGrainId());

            var ll = new Task<List<int>>[20];
            for (var i = 0; i < 2000; i++)
            {
                for (var j = 0; j < ll.Length; j++)
                    ll[j] = grain.ModifyReturnList_Test();

                Task.WhenAll(ll).Wait();
            }
        }
    }
}
