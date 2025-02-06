using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Persistence.Migration;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace Migration.Tests
{
    [TestCategory("Functionals"), TestCategory("Migration")]
    public class GrainReferenceExtractorTests : HostedTestClusterEnsureDefaultStarted
    {
        private IGrainReferenceExtractor? _target;

        public GrainReferenceExtractorTests(MigrationDefaultClusterFixture fixture)
            : base(fixture)
        {
        }

        private IGrainReferenceExtractor Target
        {
            get
            {
                if (_target == null)
                {
                    var primarySilo = ((InProcessSiloHandle)this.HostedCluster.Primary).SiloHost;
                    _target = primarySilo.Services.GetRequiredService<IGrainReferenceExtractor>();
                }
                return _target;
            }
        }

        [Fact]
        public void ConvertStringKey()
        {
            var id = "hello";
            var expected = "mystring/hello";

            var grain = this.GrainFactory.GetGrain<IMyStringGrain>(id);
            var actual = Target.GetGrainId((GrainReference)grain);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ConvertGuidKey()
        {
            var id = Guid.Parse("c53032c1-17a4-429c-a964-e61d65358214");
            var expected = "myguid/c53032c117a4429ca964e61d65358214";

            var grain = this.GrainFactory.GetGrain<IMyGuidGrain>(id);
            var actual = Target.GetGrainId((GrainReference)grain);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ConvertGuidCompoundKey()
        {
            var id = Guid.Parse("18832031-14df-4186-b6b4-1cd487b1cf7e");
            var ext = "hello";
            var expected = "myguidcompound/1883203114df4186b6b41cd487b1cf7e+hello";

            var grain = this.GrainFactory.GetGrain<IMyGuidCompoundGrain>(id, ext);
            var actual = Target.GetGrainId((GrainReference)grain);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ConvertIntegerKey()
        {
            var id = 806;
            var expected = "myinteger/326";

            var grain = this.GrainFactory.GetGrain<IMyIntegerGrain>(id);
            var actual = Target.GetGrainId((GrainReference)grain);

            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ConvertIntegerCompoundKey()
        {
            var id = 806;
            var ext = "hello";
            var expected = "myintegercompound/326+hello";

            var grain = this.GrainFactory.GetGrain<IMyIntegerCompoundGrain>(id, ext);
            var actual = Target.GetGrainId((GrainReference)grain);

            Assert.Equal(expected, actual);
        }

        [Theory]
        [InlineData(typeof(IMyStringGrain<String>), "hello", null, "mystring`1[[string]]/hello")]
        [InlineData(typeof(IMyStringGrain<String, Int32>), "hello", null, "mystring`2[[string],[int]]/hello")]
        [InlineData(typeof(IMyGuidGrain<String>), "50ff6a20-caf5-4e43-b27b-aac1825417dd", null, "myguid`1[[string]]/50ff6a20caf54e43b27baac1825417dd")]
        [InlineData(typeof(IMyGuidGrain<String, Int32>), "8b1a74a2-7cd5-4673-8df7-b69ecaf6eef3", null, "myguid`2[[string],[int]]/8b1a74a27cd546738df7b69ecaf6eef3")]
        [InlineData(typeof(IMyGuidCompoundGrain<String>), "42c9f497-0ae7-4ee6-8f07-447354ec4812", "hello", "myguidcompound`1[[string]]/42c9f4970ae74ee68f07447354ec4812+hello")]
        [InlineData(typeof(IMyGuidCompoundGrain<String, Int32>), "02fbd5e7-05c2-4e9b-b7de-807d1a2bbbad", "hello", "myguidcompound`2[[string],[int]]/02fbd5e705c24e9bb7de807d1a2bbbad+hello")]
        [InlineData(typeof(IMyIntegerGrain<String>), "806", null, "myinteger`1[[string]]/326")]
        [InlineData(typeof(IMyIntegerGrain<String, Int32>), "806", null, "myinteger`2[[string],[int]]/326")]
        [InlineData(typeof(IMyIntegerCompoundGrain<String>), "806", "hello", "myintegercompound`1[[string]]/326+hello")]
        [InlineData(typeof(IMyIntegerCompoundGrain<String, Int32>), "806", "hello", "myintegercompound`2[[string],[int]]/326+hello")]
        public void ConvertGenericGrain(Type grainInterfaceType, string key, string keyExt, string expected)
        {
            var grain = this.GrainFactory.GetTestGrain(grainInterfaceType, key, keyExt);
            var actual = Target.GetGrainId((GrainReference)grain);
            Assert.Equal(expected, actual);
        }
    }
}