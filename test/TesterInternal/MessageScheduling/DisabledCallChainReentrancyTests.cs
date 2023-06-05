using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.General
{
    public class DisabledCallChainReentrancyTests : OrleansTestingBase, IClassFixture<DisabledCallChainReentrancyTests.Fixture>
    {
        private readonly DisabledCallChainReentrancyTestRunner runner;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<ReentrancyTestsSiloBuilderConfigurator>();
            }
        }

        public DisabledCallChainReentrancyTests(Fixture fixture)
        {
            runner = new DisabledCallChainReentrancyTestRunner(fixture.GrainFactory, fixture.Logger);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain()
        {
            runner.NonReentrantGrain(false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsFalse()
        {
            runner.NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsFalse(false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void UnorderedNonReentrantGrain()
        {
            runner.UnorderedNonReentrantGrain(false);
        }
    }
}