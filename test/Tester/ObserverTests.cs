using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Tester;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace UnitTests.General
{
    /// <summary>
    /// Summary description for ObserverTests
    /// </summary>
    public class ObserverTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly TimeSpan timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(10);
        private int callbackCounter;
        private readonly bool[] callbacksRecieved = new bool[2];

        // we keep the observer objects as instance variables to prevent them from
        // being garbage collected permaturely (the runtime stores them as weak references).
        private SimpleGrainObserver observer1;
        private SimpleGrainObserver observer2;
        
        public void TestInitialize()
        {
            callbackCounter = 0;
            callbacksRecieved[0] = false;
            callbacksRecieved[1] = false;

            observer1 = null;
            observer2 = null;
        }

        private ISimpleObserverableGrain GetGrain()
        {
            return GrainFactory.GetGrain<ISimpleObserverableGrain>(GetRandomGrainId());
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ObserverTest_SimpleNotification()
        {
            TestInitialize();
            var result = new AsyncResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            observer1 = new SimpleGrainObserver(ObserverTest_SimpleNotification_Callback, result);
            ISimpleGrainObserver reference = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(this.observer1);
            await grain.Subscribe(reference);
            await grain.SetA(3);
            await grain.SetB(2);

            Assert.IsTrue(await result.WaitForFinished(timeout), "Should not timeout waiting {0} for {1}", timeout, "SetB");

            await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ObserverTest_SimpleNotification_GeneratedFactory()
        {
            TestInitialize();
            var result = new AsyncResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            observer1 = new SimpleGrainObserver(ObserverTest_SimpleNotification_Callback, result);
            ISimpleGrainObserver reference = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer1);
            await grain.Subscribe(reference);
            await grain.SetA(3);
            await grain.SetB(2);

            Assert.IsTrue(await result.WaitForFinished(timeout), "Should not timeout waiting {0} for {1}", timeout, "SetB");

            await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }

        void ObserverTest_SimpleNotification_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            logger.Info("Invoking ObserverTest_SimpleNotification_Callback for {0} time with a = {1} and b = {2}", callbackCounter, a, b);

            if (a == 3 && b == 0)
                callbacksRecieved[0] = true;
            else if (a == 3 && b == 2)
                callbacksRecieved[1] = true;
            else
                throw new ArgumentOutOfRangeException("Unexpected callback with values: a=" + a + ",b=" + b);

            if (callbackCounter == 1)
            {
                // Allow for callbacks occurring in any order
                Assert.IsTrue(callbacksRecieved[0] || callbacksRecieved[1], "Received one callback ok");
            }
            else if (callbackCounter == 2)
            {
                Assert.IsTrue(callbacksRecieved[0] && callbacksRecieved[1], "Received two callbacks ok");
                result.Done = true;
            }
            else
            {
                Assert.Fail("Callback has been called more times than was expected.");
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ObserverTest_DoubleSubscriptionSameReference()
        {
            TestInitialize();
            var result = new AsyncResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            observer1 = new SimpleGrainObserver(ObserverTest_DoubleSubscriptionSameReference_Callback, result);
            ISimpleGrainObserver reference = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer1);
            await grain.Subscribe(reference);
            await grain.SetA(1); // Use grain
            try
            {
                await grain.Subscribe(reference);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception exc)
            {
                Exception baseException = exc.GetBaseException();
                logger.Info("Received exception: {0}", baseException);
                Assert.IsInstanceOfType(baseException, typeof(OrleansException));
                if (!baseException.Message.StartsWith("Cannot subscribe already subscribed observer"))
                {
                    Assert.Fail("Unexpected exception message: " + baseException);
                }
            }
            await grain.SetA(2); // Use grain

            Assert.IsFalse(await result.WaitForFinished(timeout), "Should timeout waiting {0} for {1}", timeout, "SetA(2)");

            await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }

        void ObserverTest_DoubleSubscriptionSameReference_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            logger.Info("Invoking ObserverTest_DoubleSubscriptionSameReference_Callback for {0} time with a={1} and b={2}", callbackCounter, a, b);
            Assert.IsTrue(callbackCounter <= 2, "Callback has been called more times than was expected {0}", callbackCounter);

            if (callbackCounter == 2)
            {
                result.Continue = true;
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ObserverTest_SubscribeUnsubscribe()
        {
            TestInitialize();
            var result = new AsyncResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            observer1 = new SimpleGrainObserver(ObserverTest_SubscribeUnsubscribe_Callback, result);
            ISimpleGrainObserver reference = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer1);
            await grain.Subscribe(reference);
            await grain.SetA(5);
            Assert.IsTrue(await result.WaitForContinue(timeout), "Should not timeout waiting {0} for {1}", timeout, "SetA");
            await grain.Unsubscribe(reference);
            await grain.SetB(3);

            Assert.IsFalse(await result.WaitForFinished(timeout), "Should timeout waiting {0} for {1}", timeout, "SetB");

            await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }

        void ObserverTest_SubscribeUnsubscribe_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            logger.Info("Invoking ObserverTest_SubscribeUnsubscribe_Callback for {0} time with a = {1} and b = {2}", callbackCounter, a, b);
            Assert.IsTrue(callbackCounter < 2, "Callback has been called more times than was expected.");

            Assert.AreEqual(5, a);
            Assert.AreEqual(0, b);

            result.Continue = true;
        }


        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ObserverTest_Unsubscribe()
        {
            TestInitialize();
            ISimpleObserverableGrain grain = GetGrain();
            observer1 = new SimpleGrainObserver(null, null);
            ISimpleGrainObserver reference = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer1);
            try
            {
                await grain.Unsubscribe(reference);

                await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception exc)
            {
                Exception baseException = exc.GetBaseException();
                if (!(baseException is OrleansException))
                    Assert.Fail("Unexpected exception type {0}", baseException);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ObserverTest_DoubleSubscriptionDifferentReferences()
        {
            TestInitialize();
            var result = new AsyncResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            observer1 = new SimpleGrainObserver(ObserverTest_DoubleSubscriptionDifferentReferences_Callback, result);
            ISimpleGrainObserver reference1 = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer1);
            observer2 = new SimpleGrainObserver(ObserverTest_DoubleSubscriptionDifferentReferences_Callback, result);
            ISimpleGrainObserver reference2 = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer2);
            await grain.Subscribe(reference1);
            await grain.Subscribe(reference2);
            grain.SetA(6).Ignore();

            Assert.IsTrue(await result.WaitForFinished(timeout), "Should not timeout waiting {0} for {1}", timeout, "SetA");

            await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference1);
            await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference2);
        }

        void ObserverTest_DoubleSubscriptionDifferentReferences_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            logger.Info("Invoking ObserverTest_DoubleSubscriptionDifferentReferences_Callback for {0} time with a = {1} and b = {2}", callbackCounter, a, b);
            Assert.IsTrue(callbackCounter < 3, "Callback has been called more times than was expected.");

            Assert.AreEqual(6, a);
            Assert.AreEqual(0, b);

            if (callbackCounter == 2)
                result.Done = true;
        }


        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ObserverTest_DeleteObject()
        {
            TestInitialize();
            var result = new AsyncResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            observer1 = new SimpleGrainObserver(ObserverTest_DeleteObject_Callback, result);
            ISimpleGrainObserver reference = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer1);
            await grain.Subscribe(reference);
            await grain.SetA(5);
            Assert.IsTrue(await result.WaitForContinue(timeout), "Should not timeout waiting {0} for {1}", timeout, "SetA");
            await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
            await grain.SetB(3);

            Assert.IsFalse(await result.WaitForFinished(timeout), "Should timeout waiting {0} for {1}", timeout, "SetB");
        }

        void ObserverTest_DeleteObject_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            logger.Info("Invoking ObserverTest_DeleteObject_Callback for {0} time with a = {1} and b = {2}", callbackCounter, a, b);
            Assert.IsTrue(callbackCounter < 2, "Callback has been called more times than was expected.");

            Assert.AreEqual(5, a);
            Assert.AreEqual(0, b);

            result.Continue = true;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional")]
        public async Task ObserverTest_SubscriberMustBeGrainReference()
        {
            TestInitialize();
            await Xunit.Assert.ThrowsAsync(typeof(NotSupportedException), async () =>
            {
                var result = new AsyncResultHandle();

                ISimpleObserverableGrain grain = GetGrain();
                observer1 = new SimpleGrainObserver(ObserverTest_SimpleNotification_Callback, result);
                ISimpleGrainObserver reference = observer1;
                // Should be: GrainFactory.CreateObjectReference<ISimpleGrainObserver>(obj);
                await grain.Subscribe(reference);
                // Not reached
            });
        }

        internal class SimpleGrainObserver : ISimpleGrainObserver
        {
            readonly Action<int, int, AsyncResultHandle> action;
            readonly AsyncResultHandle result;

            public SimpleGrainObserver(Action<int, int, AsyncResultHandle> action, AsyncResultHandle result)
            {
                this.action = action;
                this.result = result;
            }

            #region ISimpleGrainObserver Members

            public void StateChanged(int a, int b)
            {
                GrainClient.Logger.Verbose("SimpleGrainObserver.StateChanged a={0} b={1}", a, b);
                if (action != null)
                {
                    action(a, b, result);
                }
            }

            #endregion
        }
    }
}
