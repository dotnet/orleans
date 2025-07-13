using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests for multifaceted grain functionality in Orleans.
    /// Multifaceted grains can implement multiple interfaces and be accessed
    /// through different interface references using AsReference<T>().
    /// This enables grains to expose different APIs to different consumers
    /// while maintaining a single grain instance.
    /// </summary>
    //using ValueUpdateEventArgs = MultifacetGrainClient.ValueUpdateEventArgs;
    public class MultifacetGrainTest : HostedTestClusterEnsureDefaultStarted
    {
        private IMultifacetWriter writer;
        private IMultifacetReader reader;

        //int eventCounter;
        private const int EXPECTED_NUMBER_OF_EVENTS = 4;
        private readonly TimeSpan timeout = TimeSpan.FromSeconds(5);

        public MultifacetGrainTest(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests accessing a grain through different interface facets.
        /// Verifies that a grain implementing multiple interfaces (writer and reader)
        /// can be accessed through either interface using AsReference<T>(),
        /// and that both references operate on the same underlying grain state.
        /// </summary>
        [Fact, TestCategory("Functional"), TestCategory("Cast")]
        public async Task RWReferences()
        {
            writer = this.GrainFactory.GetGrain<IMultifacetWriter>(GetRandomGrainId());
            reader = writer.AsReference<IMultifacetReader>();

            int x = 1234;
            await writer.SetValue(x).WaitAsync(timeout);
            int y = await reader.GetValue();
            Assert.Equal(x, y);
        }

        /// <summary>
        /// Verifies that invalid interface casts throw appropriate exceptions.
        /// Tests that attempting to cast a grain reference to an interface
        /// it doesn't implement results in an InvalidCastException,
        /// ensuring type safety in the multifacet pattern.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public void RWReferencesInvalidCastException()
        {
            Assert.Throws<InvalidCastException>(() =>
            {
                reader = this.GrainFactory.GetGrain<IMultifacetReader>(GetRandomGrainId());
                writer = (IMultifacetWriter)reader;
            });
        }

        /// <summary>
        /// Tests factory methods that return different facets of a grain.
        /// Verifies that factory grains can create and return references
        /// to different interfaces of the same grain, enabling patterns
        /// where grain creation logic returns appropriate interfaces to callers.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task MultifacetFactory()
        {
            IMultifacetFactoryTestGrain factory = this.GrainFactory.GetGrain<IMultifacetFactoryTestGrain>(GetRandomGrainId());
            IMultifacetTestGrain grain = this.GrainFactory.GetGrain<IMultifacetTestGrain>(GetRandomGrainId());
            IMultifacetWriter writer = await factory.GetWriter(grain /*"MultifacetFactory"*/);
            IMultifacetReader reader = await factory.GetReader(grain /*"MultifacetFactory"*/);
            await writer.SetValue(5);
            int v = await reader.GetValue();
            Assert.Equal(5, v);

        }

        /// <summary>
        /// Tests passing grain interface references as method arguments.
        /// Verifies that different interface facets of a grain can be passed
        /// to and stored by other grains, and that these references remain
        /// valid and operate on the same underlying grain instance.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Cast")]
        public async Task Multifacet_InterfacesAsArguments()
        {
            IMultifacetFactoryTestGrain factory = this.GrainFactory.GetGrain<IMultifacetFactoryTestGrain>(GetRandomGrainId());
            IMultifacetTestGrain grain = this.GrainFactory.GetGrain<IMultifacetTestGrain>(GetRandomGrainId());
            await factory.SetReader(grain);
            await factory.SetWriter(grain);
            IMultifacetWriter writer = await factory.GetWriter();
            IMultifacetReader reader = await factory.GetReader();
            await writer.SetValue(10);
            int v = await reader.GetValue();
            Assert.Equal(10, v);
        }
    }
}
