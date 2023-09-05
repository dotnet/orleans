using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Microsoft.Extensions.Logging;

namespace UnitTests
{
    public class DisabledCallChainReentrancyTestRunner
    {
        private readonly IGrainFactory grainFactory;
        private readonly ILogger logger;

        public DisabledCallChainReentrancyTestRunner(IGrainFactory grainFactory, ILogger logger)
        {
            this.grainFactory = grainFactory;
            this.logger = logger;
        }

        public void NonReentrantGrain(bool performDeadlockDetection)
        {
            INonReentrantGrain nonreentrant = this.grainFactory.GetGrain<INonReentrantGrain>(OrleansTestingBase.GetRandomGrainId());
            nonreentrant.SetSelf(nonreentrant).Wait();
            bool timeout = false;
            bool deadlock = false;
            try
            {
                timeout = !nonreentrant.Two().Wait(2000);
            }
            catch (Exception exc)
            {
                Assert.True(false, string.Format("Unexpected exception {0}: {1}", exc.Message, exc.StackTrace));
            }
            if (performDeadlockDetection)
            {
                Assert.True(deadlock, "Non-reentrant grain should deadlock");
            }
            else
            {
                Assert.True(timeout, "Non-reentrant grain should timeout");
            }
            this.logger.LogInformation("Reentrancy NonReentrantGrain Test finished OK.");
        }

        public void NonReentrantGrain_WithMayInterleaveStaticPredicate_WhenPredicateReturnsFalse(bool performDeadlockDetection)
        {
            var grain = this.grainFactory.GetGrain<IMayInterleaveStaticPredicateGrain>(OrleansTestingBase.GetRandomGrainId());
            grain.SetSelf(grain).Wait();
            bool timeout = false;
            bool deadlock = false;
            try
            {
                timeout = !grain.Two().Wait(2000);
            }
            catch (Exception exc)
            {
                Assert.True(false, string.Format("Unexpected exception {0}: {1}", exc.Message, exc.StackTrace));
            }
            if (performDeadlockDetection)
            {
                Assert.True(deadlock, "Non-reentrant grain should deadlock when MayInterleave predicate returns false");
            }
            else
            {
                Assert.True(timeout, "Non-reentrant grain should timeout when MayInterleave predicate returns false");
            }
            this.logger.LogInformation("Reentrancy NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsFalse Test finished OK.");
        }

        public void NonReentrantGrain_WithMayInterleaveInstancedPredicate_WhenPredicateReturnsFalse(bool performDeadlockDetection)
        {
            var grain = this.grainFactory.GetGrain<IMayInterleaveInstancedPredicateGrain>(OrleansTestingBase.GetRandomGrainId());
            grain.SetSelf(grain).Wait();
            bool timeout = false;
            bool deadlock = false;
            try
            {
                timeout = !grain.Two().Wait(2000);
            }
            catch (Exception exc)
            {
                Assert.True(false, string.Format("Unexpected exception {0}: {1}", exc.Message, exc.StackTrace));
            }
            if (performDeadlockDetection)
            {
                Assert.True(deadlock, "Non-reentrant grain should deadlock when MayInterleave predicate returns false");
            }
            else
            {
                Assert.True(timeout, "Non-reentrant grain should timeout when MayInterleave predicate returns false");
            }
            this.logger.LogInformation("Reentrancy NonReentrantGrain_WithMayInterleaveInstancedPredicate_WhenPredicateReturnsFalse Test finished OK.");
        }

        public void UnorderedNonReentrantGrain(bool performDeadlockDetection)
        {
            IUnorderedNonReentrantGrain unonreentrant = this.grainFactory.GetGrain<IUnorderedNonReentrantGrain>(OrleansTestingBase.GetRandomGrainId());
            unonreentrant.SetSelf(unonreentrant).Wait();
            bool timeout = false;
            bool deadlock = false;
            try
            {
                timeout = !unonreentrant.Two().Wait(2000);
            }
            catch (Exception exc)
            {
                Assert.True(false, $"Unexpected exception {exc.Message}: {exc.StackTrace}");
            }
            if (performDeadlockDetection)
            {
                Assert.True(deadlock, "Non-reentrant grain should deadlock");
            }
            else
            {
                Assert.True(timeout, "Non-reentrant grain should timeout");
            }

            this.logger.LogInformation("Reentrancy UnorderedNonReentrantGrain Test finished OK.");
        }
    }
}