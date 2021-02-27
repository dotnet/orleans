using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests
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
        public void NonReentrantGrain_WithMessageInterleavesPredicate_StreamItemDelivery_WhenPredicateReturnsFalse()
        {
            this.runner.NonReentrantGrain_WithMessageInterleavesPredicate_StreamItemDelivery_WhenPredicateReturnsFalse(false);
        }       
       

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain()
        {
            this.runner.NonReentrantGrain(false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsFalse()
        {
            this.runner.NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsFalse(false);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void UnorderedNonReentrantGrain()
        {
            this.runner.UnorderedNonReentrantGrain(false);
        }
    }
}