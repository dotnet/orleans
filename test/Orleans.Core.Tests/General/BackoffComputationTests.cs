using System;
using System.Collections.Generic;
using Orleans.Internal;
using Xunit;

namespace UnitTests.General
{
    [TestCategory("BVT"), TestCategory("Functional")]
    public class BackoffComputationTests
    {
        private static readonly TimeSpan BaseMin = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan BaseMax = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan Cap = TimeSpan.FromSeconds(80);

        [Fact]
        public void ComputeBackoffDelay_FirstFailure_IsBetweenBaseMinAndBaseMax()
        {
            for (var i = 0; i < 50; i++)
            {
                var delay = BackoffComputation.ComputeBackoffDelay(1, BaseMin, BaseMax, Cap);
                Assert.InRange(delay, BaseMin, BaseMax);
            }
        }

        [Fact]
        public void ComputeBackoffDelay_ZeroOrNegativeFailureCount_TreatedAsFirstFailure()
        {
            foreach (var count in new[] { 0, -1, -100, int.MinValue })
            {
                var delay = BackoffComputation.ComputeBackoffDelay(count, BaseMin, BaseMax, Cap);
                Assert.InRange(delay, BaseMin, BaseMax);
            }
        }

        [Fact]
        public void ComputeBackoffDelay_BothBoundsDoubleEachFailure_UntilCap()
        {
            var expectations = new (int failures, TimeSpan min, TimeSpan max)[]
            {
                (1, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)),
                (2, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40)),
                (3, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(80)),
            };

            foreach (var (failures, min, max) in expectations)
            {
                for (var i = 0; i < 30; i++)
                {
                    var delay = BackoffComputation.ComputeBackoffDelay(failures, BaseMin, BaseMax, Cap);
                    Assert.InRange(delay, min, max);
                }
            }
        }

        [Fact]
        public void ComputeBackoffDelay_SaturatesAtCappedWindow()
        {
            foreach (var failures in new[] { 3, 4, 9, 20, 100, 1_000_000, int.MaxValue })
            {
                for (var i = 0; i < 20; i++)
                {
                    var delay = BackoffComputation.ComputeBackoffDelay(failures, BaseMin, BaseMax, Cap);
                    Assert.InRange(delay, TimeSpan.FromTicks(Cap.Ticks / 2), Cap);
                }
            }
        }

        [Fact]
        public void ComputeBackoffDelay_ProducesJitterAcrossInvocations()
        {
            var observed = new HashSet<TimeSpan>();
            for (var i = 0; i < 50; i++)
            {
                observed.Add(BackoffComputation.ComputeBackoffDelay(3, BaseMin, BaseMax, Cap));
            }

            Assert.True(observed.Count > 1, "Expected multiple distinct delays due to jitter.");
        }

        [Fact]
        public void ComputeBackoffDelay_LowerBoundShiftsUpward_DefeatsThunderingHerd()
        {
            for (var i = 0; i < 100; i++)
            {
                var delay = BackoffComputation.ComputeBackoffDelay(2, BaseMin, BaseMax, Cap);
                Assert.True(delay >= BaseMax, $"Expected delay >= {BaseMax}, but got {delay}.");
            }
        }

        [Fact]
        public void ComputeBackoffDelay_InvalidArguments_Throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BackoffComputation.ComputeBackoffDelay(1, TimeSpan.Zero, BaseMax, Cap));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BackoffComputation.ComputeBackoffDelay(1, TimeSpan.FromSeconds(-1), BaseMax, Cap));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BackoffComputation.ComputeBackoffDelay(1, BaseMin, BaseMin, Cap));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BackoffComputation.ComputeBackoffDelay(1, BaseMin, BaseMax, TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void ComputeBackoffDelay_BoundsEqualCap_SaturatesImmediately()
        {
            var min = TimeSpan.FromSeconds(5);
            var max = TimeSpan.FromSeconds(10);
            var cap = TimeSpan.FromSeconds(10);

            for (var failures = 1; failures < 5; failures++)
            {
                for (var i = 0; i < 20; i++)
                {
                    var delay = BackoffComputation.ComputeBackoffDelay(failures, min, max, cap);
                    if (failures == 1)
                    {
                        Assert.InRange(delay, min, cap);
                    }
                    else
                    {
                        Assert.InRange(delay, TimeSpan.FromTicks(cap.Ticks / 2), cap);
                    }
                }
            }
        }
    }
}
