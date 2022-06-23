using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Summary description for ObserverTests
    /// </summary>
    public class ObserverTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly TimeSpan timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(10);
        private int callbackCounter;
        private readonly bool[] callbacksReceived = new bool[2];

        // we keep the observer objects as instance variables to prevent them from
        // being garbage collected permaturely (the runtime stores them as weak references).
        private SimpleGrainObserver observer1;
        private SimpleGrainObserver observer2;

        public ObserverTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        private void TestInitialize()
        {
            callbackCounter = 0;
            callbacksReceived[0] = false;
            callbacksReceived[1] = false;

            observer1 = null;
            observer2 = null;
        }

        private ISimpleObserverableGrain GetGrain()
        {
            return this.GrainFactory.GetGrain<ISimpleObserverableGrain>(GetRandomGrainId());
        }

        [Fact, TestCategory("BVT")]
        public async Task ObserverTest_SimpleNotification()
        {
            TestInitialize();
            var result = new AsyncResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(this.ObserverTest_SimpleNotification_Callback, result, this.Logger);
            ISimpleGrainObserver reference = this.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(this.observer1);
            await grain.Subscribe(reference);
            await grain.SetA(3);
            await grain.SetB(2);

            Assert.True(await result.WaitForFinished(timeout));

            this.GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }

        [Fact, TestCategory("BVT")]
        public async Task ObserverTest_SimpleNotification_GeneratedFactory()
        {
            TestInitialize();
            var result = new AsyncResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(this.ObserverTest_SimpleNotification_Callback, result, this.Logger);
            ISimpleGrainObserver reference = this.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer1);
            await grain.Subscribe(reference);
            await grain.SetA(3);
            await grain.SetB(2);

            Assert.True(await result.WaitForFinished(timeout));

            this.GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }

        void ObserverTest_SimpleNotification_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            this.Logger.LogInformation("Invoking ObserverTest_SimpleNotification_Callback for {CallbackCounter} time with a = {A} and b = {B}", this.callbackCounter, a, b);

            if (a == 3 && b == 0)
                callbacksReceived[0] = true;
            else if (a == 3 && b == 2)
                callbacksReceived[1] = true;
            else
                throw new ArgumentOutOfRangeException("Unexpected callback with values: a=" + a + ",b=" + b);

            if (callbackCounter == 1)
            {
                // Allow for callbacks occurring in any order
                Assert.True(callbacksReceived[0] || callbacksReceived[1]);
            }
            else if (callbackCounter == 2)
            {
                Assert.True(callbacksReceived[0] && callbacksReceived[1]);
                result.Done = true;
            }
            else
            {
                Assert.True(false);
            }
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional")]
        public async Task ObserverTest_DoubleSubscriptionSameReference()
        {
            TestInitialize();
            var result = new AsyncResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(this.ObserverTest_DoubleSubscriptionSameReference_Callback, result, this.Logger);
            ISimpleGrainObserver reference = this.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer1);
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
                this.Logger.LogInformation(baseException, "Received exception");
                Assert.IsAssignableFrom<OrleansException>(baseException);
                if (!baseException.Message.StartsWith("Cannot subscribe already subscribed observer"))
                {
                    Assert.True(false, "Unexpected exception message: " + baseException);
                }
            }

            await grain.SetA(2); // Use grain

            Assert.False(await result.WaitForFinished(timeout), $"Should timeout waiting {timeout} for SetA(2)");

            this.GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }

        void ObserverTest_DoubleSubscriptionSameReference_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            this.Logger.LogInformation("Invoking ObserverTest_DoubleSubscriptionSameReference_Callback for {CallbackCounter} time with a={A} and b={B}", this.callbackCounter, a, b);
            Assert.True(callbackCounter <= 2, "Callback has been called more times than was expected " + callbackCounter);
            if (callbackCounter == 2)
            {
                result.Continue = true;
            }
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional")]
        public async Task ObserverTest_SubscribeUnsubscribe()
        {
            TestInitialize();
            var result = new AsyncResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(this.ObserverTest_SubscribeUnsubscribe_Callback, result, this.Logger);
            ISimpleGrainObserver reference = this.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer1);
            await grain.Subscribe(reference);
            await grain.SetA(5);
            Assert.True(await result.WaitForContinue(timeout), $"Should not timeout waiting {timeout} for SetA");

            await grain.Unsubscribe(reference);
            await grain.SetB(3);

            Assert.False(await result.WaitForFinished(timeout), $"Should timeout waiting {timeout} for SetB");

            this.GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }

        void ObserverTest_SubscribeUnsubscribe_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            this.Logger.LogInformation("Invoking ObserverTest_SubscribeUnsubscribe_Callback for {CallbackCounter} time with a = {A} and b = {B}", this.callbackCounter, a, b);
            Assert.True(callbackCounter < 2, "Callback has been called more times than was expected.");

            Assert.Equal(5, a);
            Assert.Equal(0, b);

            result.Continue = true;
        }


        [Fact, TestCategory("BVT")]
        public async Task ObserverTest_Unsubscribe()
        {
            TestInitialize();
            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(null, null, this.Logger);
            ISimpleGrainObserver reference = this.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer1);
            try
            {
                await grain.Unsubscribe(reference);

                this.GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception exc)
            {
                Exception baseException = exc.GetBaseException();
                if (!(baseException is OrleansException))
                    Assert.True(false);
            }
        }

        [Fact, TestCategory("BVT")]
        public async Task ObserverTest_DoubleSubscriptionDifferentReferences()
        {
            TestInitialize();
            var result = new AsyncResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(this.ObserverTest_DoubleSubscriptionDifferentReferences_Callback, result, this.Logger);
            ISimpleGrainObserver reference1 = this.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer1);
            this.observer2 = new SimpleGrainObserver(this.ObserverTest_DoubleSubscriptionDifferentReferences_Callback, result, this.Logger);
            ISimpleGrainObserver reference2 = this.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer2);
            await grain.Subscribe(reference1);
            await grain.Subscribe(reference2);
            grain.SetA(6).Ignore();

            Assert.True(await result.WaitForFinished(timeout), $"Should not timeout waiting {timeout} for SetA");

            this.GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference1);
            this.GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference2);
        }

        void ObserverTest_DoubleSubscriptionDifferentReferences_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            this.Logger.LogInformation("Invoking ObserverTest_DoubleSubscriptionDifferentReferences_Callback for {CallbackCounter} time with a = {A} and b = {B}", this.callbackCounter, a, b);
            Assert.True(callbackCounter < 3, "Callback has been called more times than was expected.");

            Assert.Equal(6, a);
            Assert.Equal(0, b);

            if (callbackCounter == 2)
                result.Done = true;
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional")]
        public async Task ObserverTest_DeleteObject()
        {
            TestInitialize();
            var result = new AsyncResultHandle();

            ISimpleObserverableGrain grain = GetGrain();
            this.observer1 = new SimpleGrainObserver(this.ObserverTest_DeleteObject_Callback, result, this.Logger);
            ISimpleGrainObserver reference = this.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(observer1);
            await grain.Subscribe(reference);
            await grain.SetA(5);
            Assert.True(await result.WaitForContinue(timeout), $"Should not timeout waiting {timeout} for SetA");
            this.GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
            await grain.SetB(3);

            Assert.False(await result.WaitForFinished(timeout), $"Should timeout waiting {timeout} for SetB");
        }

        void ObserverTest_DeleteObject_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            this.Logger.LogInformation("Invoking ObserverTest_DeleteObject_Callback for {CallbackCounter} time with a = {A} and b = {B}", this.callbackCounter, a, b);
            Assert.True(callbackCounter < 2, "Callback has been called more times than was expected.");

            Assert.Equal(5, a);
            Assert.Equal(0, b);

            result.Continue = true;
        }

        [Fact, TestCategory("BVT")]
        public async Task ObserverTest_SubscriberMustBeGrainReference()
        {
            TestInitialize();
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                var result = new AsyncResultHandle();

                ISimpleObserverableGrain grain = this.GetGrain();
                this.observer1 = new SimpleGrainObserver(this.ObserverTest_SimpleNotification_Callback, result, this.Logger);
                ISimpleGrainObserver reference = this.observer1;
                // Should be: this.GrainFactory.CreateObjectReference<ISimpleGrainObserver>(obj);
                await grain.Subscribe(reference);
                // Not reached
            });
        }

        [Fact, TestCategory("BVT")]
        public void ObserverTest_CreateObjectReference_ThrowsForInvalidArgumentTypes()
        {
            TestInitialize();

            // Attempt to create an object reference to a Grain class.
            Assert.Throws<ArgumentException>(() => this.Client.CreateObjectReference<ISimpleGrainObserver>(new DummyObserverGrain()));

            // Attempt to create an object reference to an existing GrainReference.
            var observer = new DummyObserver();
            var reference = this.Client.CreateObjectReference<ISimpleGrainObserver>(observer);
            Assert.Throws<ArgumentException>(() => this.Client.CreateObjectReference<ISimpleGrainObserver>(reference));
        }

        public class DummyObserverGrain : Grain, ISimpleGrainObserver
        {
            public void StateChanged(int a, int b) { }
        }

        public class DummyObserver : ISimpleGrainObserver
        {
            public void StateChanged(int a, int b) { }
        }

        internal class SimpleGrainObserver : ISimpleGrainObserver
        {
            readonly Action<int, int, AsyncResultHandle> action;
            readonly AsyncResultHandle result;

            private readonly ILogger logger;

            public SimpleGrainObserver(Action<int, int, AsyncResultHandle> action, AsyncResultHandle result, ILogger logger)
            {
                this.action = action;
                this.result = result;
                this.logger = logger;
            }

            public void StateChanged(int a, int b)
            {
                this.logger.LogDebug("SimpleGrainObserver.StateChanged a={A} b={B}", a, b);
                action?.Invoke(a, b, result);
            }
        }
    }
}
