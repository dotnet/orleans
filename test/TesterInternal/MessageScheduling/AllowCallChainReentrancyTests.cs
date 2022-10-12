using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General
{
    public class AllowCallChainReentrancyTests : OrleansTestingBase, IClassFixture<AllowCallChainReentrancyTests.Fixture>
    {
        private const int NumIterations = 30;
        private readonly CallChainReentrancyTestHelper _testHelper;

        public class Fixture : BaseTestClusterFixture
        {
        }

        public AllowCallChainReentrancyTests(ITestOutputHelper output, Fixture fixture)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));

            _testHelper = new CallChainReentrancyTestHelper
            {
                Fixture = fixture,
                NumIterations = NumIterations
            };
        }

        // 1) Allowed reentrancy A, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task DeadlockDetection_1()
        {
            using var _ = RequestContext.AllowCallChainReentrancy();
            await _testHelper.CallChainReentrancy_1();
        }

        // 2) Allowed reentrancy on non-reentrant grains A, B, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task CallChainReentrancy_2()
        {
            using var _ = RequestContext.AllowCallChainReentrancy();
            await _testHelper.CallChainReentrancy_2();
        }

        // 3) Allowed reentrancy X, A, X, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task CallChainReentrancy_3()
        {
            using var _ = RequestContext.AllowCallChainReentrancy();
            await _testHelper.CallChainReentrancy_3();
        }

        // 4) No Deadlock X, X
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task CallChainReentrancy_4()
        {
            using var _ = RequestContext.AllowCallChainReentrancy();
            await _testHelper.CallChainReentrancy_4();
        }

        // 5) No Deadlock X, A, X
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task CallChainReentrancy_5()
        {
            using var _ = RequestContext.AllowCallChainReentrancy();
            await _testHelper.CallChainReentrancy_5();
        }

        // 6) Allowed reentrancy on non-reentrant grains A, B, C, A
        [Fact, TestCategory("Functional"), TestCategory("Deadlock")]
        public async Task CallChainReentrancy_6()
        {
            using var _ = RequestContext.AllowCallChainReentrancy();
            await _testHelper.CallChainReentrancy_6();
        }
    }
} 
