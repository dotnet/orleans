using System;
using Orleans.Runtime.MembershipService;
using Xunit;

namespace NonSilo.Tests.Membership
{
    [TestCategory("BVT"), TestCategory("Membership")]
    public class MembershipBackoffTests
    {
        private static readonly TimeSpan ExpectedFirstFailureMin = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ExpectedFirstFailureMax = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan ExpectedSaturatedMin = TimeSpan.FromSeconds(32);
        private static readonly TimeSpan ExpectedSaturatedMax = TimeSpan.FromSeconds(64);

        [Fact]
        public void MembershipTableManager_FirstFailure_BacksOffAtLeastTwoSeconds()
        {
            for (var i = 0; i < 50; i++)
            {
                var delay = MembershipTableManager.ComputeMembershipBackoffDelay(consecutiveFailures: 1);
                Assert.InRange(delay, ExpectedFirstFailureMin, ExpectedFirstFailureMax);
            }
        }

        [Fact]
        public void MembershipTableManager_GrowsExponentially_DoublingBothBounds()
        {
            var expectations = new (int failures, TimeSpan min, TimeSpan max)[]
            {
                (1, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)),
                (2, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)),
                (3, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)),
                (4, TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(32)),
                (5, TimeSpan.FromSeconds(32), TimeSpan.FromSeconds(64)),
            };

            foreach (var (failures, min, max) in expectations)
            {
                for (var i = 0; i < 20; i++)
                {
                    var delay = MembershipTableManager.ComputeMembershipBackoffDelay(failures);
                    Assert.InRange(delay, min, max);
                }
            }
        }

        [Fact]
        public void MembershipTableManager_SaturatesAtBoundedCap()
        {
            foreach (var failures in new[] { 5, 6, 10, 100, int.MaxValue })
            {
                for (var i = 0; i < 20; i++)
                {
                    var delay = MembershipTableManager.ComputeMembershipBackoffDelay(failures);
                    Assert.InRange(delay, ExpectedSaturatedMin, ExpectedSaturatedMax);
                }
            }
        }

        [Fact]
        public void MembershipTableManager_ZeroOrNegativeFailureCount_TreatedAsFirstFailure()
        {
            foreach (var count in new[] { 0, -1, -100 })
            {
                var delay = MembershipTableManager.ComputeMembershipBackoffDelay(count);
                Assert.InRange(delay, ExpectedFirstFailureMin, ExpectedFirstFailureMax);
            }
        }
    }
}
