using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;
using TestExtensions;
using Orleans.Hosting;
using Orleans.Configuration;

namespace UnitTests.General
{
    public class DeadlockDetectionWithAllowCallChainSingleCallReentrancyTests : OrleansTestingBase, IClassFixture<DeadlockDetectionWithAllowCallChainSingleCallReentrancyTests.Fixture>
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }

            private class SiloConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder.Configure<SchedulingOptions>(options =>
                    {
                        options.PerformDeadlockDetection = true;
                        options.CallChainReentrancy = SchedulingOptions.CallChainReentrancyStrategy.SingleCall;
                    });
                }
            }

        }

        private const int numIterations = 30;

        private readonly CallChainReentrancyTestHelper testHelper;

        public DeadlockDetectionWithAllowCallChainSingleCallReentrancyTests(Fixture fixture)
        {
            this.fixture = fixture;
            testHelper = new CallChainReentrancyTestHelper()
            {
                Random = random,
                Fixture = fixture,
                NumIterations = numIterations
            };
        }


        // 1) Allowed reentrancy A, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_1()
        {
            await testHelper.DeadlockDetection_1();
        }

        // 2) Allowed reentrancy on non-reentrant grains A, B, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_2()
        {
            await testHelper.DeadlockDetection_2();
        }

        // 3) Allowed reentrancy X, A, X, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_3()
        {
            await testHelper.DeadlockDetection_3();
        }

        // 4) No Deadlock X, X
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_4()
        {
            await testHelper.DeadlockDetection_4();
        }

        // 5) No Deadlock X, A, X
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_5()
        {
            await testHelper.DeadlockDetection_5();
        }
    }
}
