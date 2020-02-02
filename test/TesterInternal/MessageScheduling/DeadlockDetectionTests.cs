using Orleans.Hosting;
using Orleans;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using Orleans.Configuration;

namespace UnitTests
{
    public class DeadlockDetectionTests : OrleansTestingBase, IClassFixture<DeadlockDetectionTests.Fixture>
    {
        private readonly DisabledCallChainReentrancyTestRunner runner;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<ReentrancyTestsSiloBuilderConfigurator>();
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
        }

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.Configure<SchedulingOptions>(options =>
                {
                    options.AllowCallChainReentrancy = false;
                    options.PerformDeadlockDetection = true;
                });
            }
        }

        public DeadlockDetectionTests(Fixture fixture)
        {
            this.runner = new DisabledCallChainReentrancyTestRunner(fixture.GrainFactory, fixture.Logger);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain_WithMessageInterleavesPredicate_StreamItemDelivery_WhenPredicateReturnsFalse()
        {
            this.runner.NonReentrantGrain_WithMessageInterleavesPredicate_StreamItemDelivery_WhenPredicateReturnsFalse(performDeadlockDetection: true);
        }


        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain()
        {
            this.runner.NonReentrantGrain(performDeadlockDetection: true);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsFalse()
        {
            this.runner.NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsFalse(performDeadlockDetection: true);
        }

        [Fact, TestCategory("Functional"), TestCategory("Tasks"), TestCategory("Reentrancy")]
        public void UnorderedNonReentrantGrain()
        {
            this.runner.UnorderedNonReentrantGrain(performDeadlockDetection: true);
        }
    }
}