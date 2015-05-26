/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.General
{
    /// <summary>
    /// Summary description for ObserverTests
    /// </summary>
    [TestClass]
    public class ObserverTests : UnitTestSiloHost
    {
        private readonly TimeSpan timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(10);
        private int callbackCounter;
        private readonly bool[] callbacksRecieved = new bool[2];

        // we keep the observer objects as instance variables to prevent them from
        // being garbage collected permaturely (the runtime stores them as weak references).
        private SimpleGrainObserver observer1;
        private SimpleGrainObserver observer2;


        public ObserverTests()
            : base() { }

        [ClassCleanup]
        public static void MyClassCleanup()
        {
            StopAllSilos();
        }

        [TestInitialize]
        public void TestInitialize()
        {
            callbackCounter = 0;
            callbacksRecieved[0] = false;
            callbacksRecieved[1] = false;

            this.observer1 = null;
            this.observer2 = null;
        }

        private ISimpleObserverableGrain GetGrain()
        {
            return SimpleObserverableGrainFactory.GetGrain(GetRandomGrainId());
        }

        private static int GetRandomGrainId()
        {
            return random.Next();
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task ObserverTest_SimpleNotification()
        {
            ResultHandle result = new ResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(ObserverTest_SimpleNotification_Callback, result);
            ISimpleGrainObserver reference = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(this.observer1);
            grain.Subscribe(reference).Wait();
            grain.SetA(3).Wait();
            grain.SetB(2).Wait();

            Assert.IsTrue(result.WaitForFinished(timeout), "Should not timeout waiting {0} for {1}", timeout, "SetB");

            await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task ObserverTest_SimpleNotification_GeneratedFactory()
        {
            ResultHandle result = new ResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(ObserverTest_SimpleNotification_Callback, result);
            ISimpleGrainObserver reference = await SimpleGrainObserverFactory.CreateObjectReference(this.observer1);
            grain.Subscribe(reference).Wait();
            grain.SetA(3).Wait();
            grain.SetB(2).Wait();

            Assert.IsTrue(result.WaitForFinished(timeout), "Should not timeout waiting {0} for {1}", timeout, "SetB");

            await SimpleGrainObserverFactory.DeleteObjectReference(reference);
        }

        void ObserverTest_SimpleNotification_Callback(int a, int b, ResultHandle result)
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

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task ObserverTest_DoubleSubscriptionSameReference()
        {
            ResultHandle result = new ResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(ObserverTest_DoubleSubscriptionSameReference_Callback, result);
            ISimpleGrainObserver reference = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(this.observer1);
            grain.Subscribe(reference).Wait();
            grain.SetA(1).Wait(); // Use grain
            try
            {
                bool ok = grain.Subscribe(reference).Wait(timeout);
                if (!ok) throw new TimeoutException();
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception exc)
            {
                Exception baseException = exc.GetBaseException();
                Console.WriteLine("Received exception: {0}", baseException);
                Assert.IsInstanceOfType(baseException, typeof(OrleansException));
                if (!baseException.Message.StartsWith("Cannot subscribe already subscribed observer"))
                {
                    Assert.Fail("Unexpected exception message: " + baseException);
                }
            }
            grain.SetA(2).Wait(); // Use grain

            Assert.IsFalse(result.WaitForFinished(timeout), "Should timeout waiting {0} for {1}", timeout, "SetA(2)");

            await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }

        void ObserverTest_DoubleSubscriptionSameReference_Callback(int a, int b, ResultHandle result)
        {
            callbackCounter++;
            logger.Info("Invoking ObserverTest_DoubleSubscriptionSameReference_Callback for {0} time with a={1} and b={2}", callbackCounter, a, b);
            Assert.IsTrue(callbackCounter <= 2, "Callback has been called more times than was expected {0}", callbackCounter);

            if (callbackCounter == 2)
            {
                result.Continue = true;
            }
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task ObserverTest_SubscribeUnsubscribe()
        {
            ResultHandle result = new ResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(ObserverTest_SubscribeUnsubscribe_Callback, result);
            ISimpleGrainObserver reference = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(this.observer1);
            grain.Subscribe(reference).Wait();
            grain.SetA(5).Wait();
            Assert.IsTrue(result.WaitForContinue(timeout), "Should not timeout waiting {0} for {1}", timeout, "SetA");
            grain.Unsubscribe(reference).Wait();
            grain.SetB(3).Wait();

            Assert.IsFalse(result.WaitForFinished(timeout), "Should timeout waiting {0} for {1}", timeout, "SetB");

            await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }

        void ObserverTest_SubscribeUnsubscribe_Callback(int a, int b, ResultHandle result)
        {
            callbackCounter++;
            logger.Info("Invoking ObserverTest_SubscribeUnsubscribe_Callback for {0} time with a = {1} and b = {2}", callbackCounter, a, b);
            Assert.IsTrue(callbackCounter < 2, "Callback has been called more times than was expected.");

            Assert.AreEqual(5, a);
            Assert.AreEqual(0, b);

            result.Continue = true;
        }


        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task ObserverTest_Unsubscribe()
        {
            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(null, null);
            ISimpleGrainObserver reference = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(this.observer1);
            try
            {
                bool ok = grain.Unsubscribe(reference).Wait(timeout);
                if (!ok) throw new TimeoutException();

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

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task ObserverTest_DoubleSubscriptionDifferentReferences()
        {
            ResultHandle result = new ResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(ObserverTest_DoubleSubscriptionDifferentReferences_Callback, result);
            ISimpleGrainObserver reference1 = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(this.observer1);
            this.observer2 = new SimpleGrainObserver(ObserverTest_DoubleSubscriptionDifferentReferences_Callback, result);
            ISimpleGrainObserver reference2 = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(this.observer2);
            grain.Subscribe(reference1).Wait();
            grain.Subscribe(reference2).Wait();
            grain.SetA(6).Ignore();

            Assert.IsTrue(result.WaitForFinished(timeout), "Should not timeout waiting {0} for {1}", timeout, "SetA");

            await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference1);
            await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference2);
        }

        void ObserverTest_DoubleSubscriptionDifferentReferences_Callback(int a, int b, ResultHandle result)
        {
            callbackCounter++;
            logger.Info("Invoking ObserverTest_DoubleSubscriptionDifferentReferences_Callback for {0} time with a = {1} and b = {2}", callbackCounter, a, b);
            Assert.IsTrue(callbackCounter < 3, "Callback has been called more times than was expected.");

            Assert.AreEqual(6, a);
            Assert.AreEqual(0, b);

            if (callbackCounter == 2)
                result.Done = true;
        }


        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        public async Task ObserverTest_DeleteObject()
        {
            ResultHandle result = new ResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(ObserverTest_DeleteObject_Callback, result);
            ISimpleGrainObserver reference = await GrainFactory.CreateObjectReference<ISimpleGrainObserver>(this.observer1);
            grain.Subscribe(reference).Wait();
            grain.SetA(5).Wait();
            Assert.IsTrue(result.WaitForContinue(timeout), "Should not timeout waiting {0} for {1}", timeout, "SetA");
            await GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
            grain.SetB(3).Wait();

            Assert.IsFalse(result.WaitForFinished(timeout), "Should timeout waiting {0} for {1}", timeout, "SetB");
        }

        void ObserverTest_DeleteObject_Callback(int a, int b, ResultHandle result)
        {
            callbackCounter++;
            logger.Info("Invoking ObserverTest_DeleteObject_Callback for {0} time with a = {1} and b = {2}", callbackCounter, a, b);
            Assert.IsTrue(callbackCounter < 2, "Callback has been called more times than was expected.");

            Assert.AreEqual(5, a);
            Assert.AreEqual(0, b);

            result.Continue = true;
        }

        [TestMethod, TestCategory("BVT"), TestCategory("Nightly")]
        [ExpectedException(typeof(NotSupportedException))]
        public void ObserverTest_SubscriberMustBeGrainReference()
        {
            ResultHandle result = new ResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(ObserverTest_SimpleNotification_Callback, result);
            ISimpleGrainObserver reference = this.observer1;
            // Should be: GrainFactory.CreateObjectReference<ISimpleGrainObserver>(obj);
            grain.Subscribe(reference).Wait();
            // Not reached
        }

        internal class SimpleGrainObserver : ISimpleGrainObserver
        {
            readonly Action<int, int, ResultHandle> action;
            readonly ResultHandle result;

            public SimpleGrainObserver(Action<int, int, ResultHandle> action, ResultHandle result)
            {
                this.action = action;
                this.result = result;
            }

            #region ISimpleGrainObserver Members

            public void StateChanged(int a, int b)
            {
                Console.WriteLine("SimpleGrainObserver.StateChanged a={0} b={1}", a, b);
                if (action != null)
                {
                    action(a, b, result);
                }
            }

            #endregion
        }
    }
}
