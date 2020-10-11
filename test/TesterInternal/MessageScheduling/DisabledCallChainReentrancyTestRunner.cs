﻿using System;
using Orleans;
using Orleans.Runtime;
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
                Exception baseExc = exc.GetBaseException();
                if (baseExc.GetType().Equals(typeof(DeadlockException)))
                {
                    deadlock = true;
                }
                else
                {
                    Assert.True(false, string.Format("Unexpected exception {0}: {1}", exc.Message, exc.StackTrace));
                }
            }
            if (performDeadlockDetection)
            {
                Assert.True(deadlock, "Non-reentrant grain should deadlock");
            }
            else
            {
                Assert.True(timeout, "Non-reentrant grain should timeout");
            }
            this.logger.Info("Reentrancy NonReentrantGrain Test finished OK.");
        }

        public void NonReentrantGrain_WithMessageInterleavesPredicate_StreamItemDelivery_WhenPredicateReturnsFalse(bool performDeadlockDetection)
        {
            var grain = this.grainFactory.GetGrain<IMayInterleavePredicateGrain>(OrleansTestingBase.GetRandomGrainId());
            grain.SubscribeToStream().Wait();
            bool timeout = false;
            bool deadlock = false;
            try
            {
                timeout = !grain.PushToStream("foo").Wait(2000);
            }
            catch (Exception exc)
            {
                Exception baseExc = exc.GetBaseException();
                if (baseExc.GetType().Equals(typeof(DeadlockException)))
                {
                    deadlock = true;
                }
                else
                {
                    Assert.True(false, string.Format("Unexpected exception {0}: {1}", exc.Message, exc.StackTrace));
                }
            }
            if (performDeadlockDetection)
            {
                Assert.True(deadlock, "Non-reentrant grain should deadlock on stream item delivery to itself when CanInterleave predicate returns false");
            }
            else
            {
                Assert.True(timeout, "Non-reentrant grain should timeout on stream item delivery to itself when CanInterleave predicate returns false");
            }
            this.logger.Info("Reentrancy NonReentrantGrain_WithMessageInterleavesPredicate_StreamItemDelivery_WhenPredicateReturnsFalse Test finished OK.");
        }

        public void NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsFalse(bool performDeadlockDetection)
        {
            var grain = this.grainFactory.GetGrain<IMayInterleavePredicateGrain>(OrleansTestingBase.GetRandomGrainId());
            grain.SetSelf(grain).Wait();
            bool timeout = false;
            bool deadlock = false;
            try
            {
                timeout = !grain.Two().Wait(2000);
            }
            catch (Exception exc)
            {
                Exception baseExc = exc.GetBaseException();
                if (baseExc.GetType().Equals(typeof(DeadlockException)))
                {
                    deadlock = true;
                }
                else
                {
                    Assert.True(false, string.Format("Unexpected exception {0}: {1}", exc.Message, exc.StackTrace));
                }
            }
            if (performDeadlockDetection)
            {
                Assert.True(deadlock, "Non-reentrant grain should deadlock when MayInterleave predicate returns false");
            }
            else
            {
                Assert.True(timeout, "Non-reentrant grain should timeout when MayInterleave predicate returns false");
            }
            this.logger.Info("Reentrancy NonReentrantGrain_WithMayInterleavePredicate_WhenPredicateReturnsFalse Test finished OK.");
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
                Exception baseExc = exc.GetBaseException();
                if (baseExc.GetType().Equals(typeof(DeadlockException)))
                {
                    deadlock = true;
                }
                else
                {
                    Assert.True(false, $"Unexpected exception {exc.Message}: {exc.StackTrace}");
                }
            }
            if (performDeadlockDetection)
            {
                Assert.True(deadlock, "Non-reentrant grain should deadlock");
            }
            else
            {
                Assert.True(timeout, "Non-reentrant grain should timeout");
            }

            this.logger.Info("Reentrancy UnorderedNonReentrantGrain Test finished OK.");
        }
    }
}