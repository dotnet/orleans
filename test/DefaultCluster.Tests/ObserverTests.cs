using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.TestingHost.Utils;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests for the Orleans Observer pattern implementation.
    /// Observers enable grains to send notifications to clients or other grains
    /// through callback interfaces. This is Orleans' mechanism for push-based
    /// communication, allowing grains to notify interested parties of state changes
    /// or events without polling. Observers are weakly referenced to prevent
    /// memory leaks and support automatic cleanup.
    /// </summary>
    public class ObserverTests : HostedTestClusterEnsureDefaultStarted
    {
        private readonly TimeSpan timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(10);
        private int callbackCounter;
        private readonly bool[] callbacksReceived = new bool[2];

        // we keep the observer objects as instance variables to prevent them from
        // being garbage collected prematurely (the runtime stores them as weak references).
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

        /// <summary>
        /// Tests basic observer notification functionality.
        /// Verifies that a grain can notify registered observers of state changes,
        /// that multiple notifications are delivered correctly, and that the
        /// observer callback context is properly maintained.
        /// </summary>
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

        /// <summary>
        /// Tests observer notifications using generated factory methods.
        /// Similar to SimpleNotification test but uses the generated factory pattern
        /// to create observer references, verifying both approaches work identically.
        /// </summary>
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

        private void ObserverTest_SimpleNotification_Callback(int a, int b, AsyncResultHandle result)
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

        /// <summary>
        /// Tests that subscribing the same observer reference twice is prevented.
        /// Verifies that Orleans detects and rejects duplicate subscriptions
        /// to prevent duplicate notifications and maintain subscription integrity.
        /// </summary>
        [Fact, TestCategory("SlowBVT")]
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
                    Assert.Fail("Unexpected exception message: " + baseException);
                }
            }

            await grain.SetA(2); // Use grain

            Assert.False(await result.WaitForFinished(timeout), $"Should timeout waiting {timeout} for SetA(2)");

            this.GrainFactory.DeleteObjectReference<ISimpleGrainObserver>(reference);
        }

        private void ObserverTest_DoubleSubscriptionSameReference_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            this.Logger.LogInformation("Invoking ObserverTest_DoubleSubscriptionSameReference_Callback for {CallbackCounter} time with a={A} and b={B}", this.callbackCounter, a, b);
            Assert.True(callbackCounter <= 2, "Callback has been called more times than was expected " + callbackCounter);
            if (callbackCounter == 2)
            {
                result.Continue = true;
            }
        }

        /// <summary>
        /// Tests the subscribe/unsubscribe lifecycle for observers.
        /// Verifies that observers receive notifications after subscribing,
        /// stop receiving them after unsubscribing, and that the unsubscribe
        /// operation properly cleans up the subscription.
        /// </summary>
        [Fact, TestCategory("SlowBVT")]
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

        private void ObserverTest_SubscribeUnsubscribe_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            this.Logger.LogInformation("Invoking ObserverTest_SubscribeUnsubscribe_Callback for {CallbackCounter} time with a = {A} and b = {B}", this.callbackCounter, a, b);
            Assert.True(callbackCounter < 2, "Callback has been called more times than was expected.");

            Assert.Equal(5, a);
            Assert.Equal(0, b);

            result.Continue = true;
        }


        /// <summary>
        /// Tests unsubscribing an observer that was never subscribed.
        /// Verifies that attempting to unsubscribe a non-existent subscription
        /// is handled gracefully without causing system errors.
        /// </summary>
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

        /// <summary>
        /// Tests multiple different observers subscribing to the same grain.
        /// Verifies that a grain can maintain multiple observer subscriptions
        /// and correctly notify all registered observers of state changes.
        /// </summary>
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

        private void ObserverTest_DoubleSubscriptionDifferentReferences_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            this.Logger.LogInformation("Invoking ObserverTest_DoubleSubscriptionDifferentReferences_Callback for {CallbackCounter} time with a = {A} and b = {B}", this.callbackCounter, a, b);
            Assert.True(callbackCounter < 3, "Callback has been called more times than was expected.");

            Assert.Equal(6, a);
            Assert.Equal(0, b);

            if (callbackCounter == 2)
                result.Done = true;
        }

        /// <summary>
        /// Tests that deleting an observer reference stops notifications.
        /// Verifies that when an observer reference is deleted using
        /// DeleteObjectReference, the grain can no longer send notifications
        /// to that observer, demonstrating automatic cleanup behavior.
        /// </summary>
        [Fact, TestCategory("SlowBVT")]
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

        private void ObserverTest_DeleteObject_Callback(int a, int b, AsyncResultHandle result)
        {
            callbackCounter++;
            this.Logger.LogInformation("Invoking ObserverTest_DeleteObject_Callback for {CallbackCounter} time with a = {A} and b = {B}", this.callbackCounter, a, b);
            Assert.True(callbackCounter < 2, "Callback has been called more times than was expected.");

            Assert.Equal(5, a);
            Assert.Equal(0, b);

            result.Continue = true;
        }

        /// <summary>
        /// Verifies that only grain references can be used as observers.
        /// Tests that attempting to subscribe a regular object (not created via
        /// CreateObjectReference) throws an appropriate exception, enforcing
        /// the requirement that observers must be grain references.
        /// </summary>
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

        /// <summary>
        /// Tests that CreateObjectReference validates its arguments correctly.
        /// Verifies that attempting to create object references from invalid types
        /// (like Grain classes or existing grain references) throws appropriate
        /// exceptions, ensuring type safety in the observer pattern.
        /// </summary>
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
            private readonly Action<int, int, AsyncResultHandle> action;
            private readonly AsyncResultHandle result;

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
