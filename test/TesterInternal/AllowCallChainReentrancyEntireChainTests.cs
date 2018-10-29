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
    public class AllowCallChainReentrancyEntireChainTests : OrleansTestingBase
    {
        private readonly Fixture fixture;

        public class Fixture : BaseTestClusterFixture
        {
            // TODO static is really not what we want here
            public static ITestOutputHelper _output { get; set; }

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
                        options.CallChainReentrancy = SchedulingOptions.CallChainReentrancyStrategy.EntireChain;
                    }).ConfigureLogging(logging =>
                    {
                        logging.AddProvider(new TestLoggingProvider(x => _output.WriteLine(x)));
                    });

                }
            }
        }

        private const int numIterations = 30;

        private readonly CallChainReentrancyTestHelper testHelper;

        public AllowCallChainReentrancyEntireChainTests(ITestOutputHelper output)
        {
            if(output == null) throw new ArgumentNullException(nameof(output));

            Fixture._output = output;
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