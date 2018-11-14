using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General
{
    public class AllowCallChainReentrancyTests : OrleansTestingBase
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
                        options.PerformDeadlockDetection = false;
                        options.AllowCallChainReentrancy = true;
                    });

                }
            }
        }

        private const int numIterations = 30;

        private readonly CallChainReentrancyTestHelper testHelper;

        public AllowCallChainReentrancyTests(ITestOutputHelper output)
        {
            if(output == null) throw new ArgumentNullException(nameof(output));

            this.fixture = new Fixture();
            testHelper = new CallChainReentrancyTestHelper
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

        // 6) Allowed reentrancy on non-reentrant grains A, B, C, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_6()
        {
            await testHelper.DeadlockDetection_6();
        }
    }
}