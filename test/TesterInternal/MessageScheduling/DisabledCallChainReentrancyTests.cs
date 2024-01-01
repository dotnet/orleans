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
            this.runner = new DisabledCallChainReentrancyTestRunner(fixture.GrainFactory, fixture.Logger);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain()
        {
            this.runner.NonReentrantGrain(false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain_WithMayInterleaveStaticPredicate_WhenPredicateReturnsFalse()
        {
            this.runner.NonReentrantGrain_WithMayInterleaveStaticPredicate_WhenPredicateReturnsFalse(false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain_WithMayInterleaveInstancedPredicate_WhenPredicateReturnsFalse()
        {
            this.runner.NonReentrantGrain_WithMayInterleaveInstancedPredicate_WhenPredicateReturnsFalse(false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void UnorderedNonReentrantGrain()
        {
            this.runner.UnorderedNonReentrantGrain(false);
        }
    }
}