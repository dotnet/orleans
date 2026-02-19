using Microsoft.Extensions.Logging;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.ActivationsLifeCycleTests
{
    /// <summary>
    /// Tests for Orleans grain activation and deactivation lifecycle.
    /// Validates that grains properly execute OnActivateAsync and OnDeactivateAsync methods,
    /// handle activation failures, support reactivation after deactivation, and manage
    /// complex scenarios like deactivation during activation or with long-running operations.
    /// </summary>
    public class GrainActivateDeactivateTests : HostedTestClusterEnsureDefaultStarted, IDisposable
    {
        private readonly IActivateDeactivateWatcherGrain watcher;

        public GrainActivateDeactivateTests(DefaultClusterFixture fixture) : base(fixture)
        {
            watcher = this.GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);
            watcher.Clear().Wait();
        }

        public virtual void Dispose()
        {
            watcher.Clear().Wait();
        }

        /// <summary>
        /// Tests basic grain reference creation for the watcher grain.
        /// Validates that the test infrastructure's watcher grain can be properly instantiated.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public async Task WatcherGrain_GetGrain()
        {
            IActivateDeactivateWatcherGrain grain = this.GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(1);
            await grain.Clear();
        }

        /// <summary>
        /// Tests basic grain activation lifecycle.
        /// Validates that OnActivateAsync is called exactly once when a grain is first accessed.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Activate_Simple()
        {
            int id = Random.Shared.Next();
            ISimpleActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<ISimpleActivateDeactivateTestGrain>(id);

            string activation = await grain.DoSomething();

            await CheckNumActivateDeactivateCalls(1, 0, activation, "After activation");
        }

        /// <summary>
        /// Tests basic grain deactivation lifecycle.
        /// Validates that OnDeactivateAsync is called when a grain is explicitly deactivated,
        /// and that both activation and deactivation hooks are called exactly once.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Deactivate_Simple()
        {
            int id = Random.Shared.Next();
            ISimpleActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<ISimpleActivateDeactivateTestGrain>(id);

            // Activate
            string activation = await grain.DoSomething();

            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation, "After deactivation");
        }

        /// <summary>
        /// Tests grain reactivation after deactivation.
        /// Validates that a new activation is created when accessing a deactivated grain,
        /// with OnActivateAsync called again for the new activation.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Reactivate_Simple()
        {
            int id = Random.Shared.Next();
            ISimpleActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<ISimpleActivateDeactivateTestGrain>(id);

            // Activate
            string activation = await grain.DoSomething();
            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation, "After deactivation");

            // Reactivate
            string activation2 = await grain.DoSomething();

            Assert.NotEqual(activation, activation2); // New activation created after re-activate
            await CheckNumActivateDeactivateCalls(2, 1, new[] { activation, activation2 }, "After reactivation");
        }

        /// <summary>
        /// Tests activation with tail call optimization patterns.
        /// Validates that grains using tail calls in their activation logic properly execute OnActivateAsync.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Activate_TailCall()
        {
            int id = Random.Shared.Next();
            ITailCallActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<ITailCallActivateDeactivateTestGrain>(id);

            string activation = await grain.DoSomething();

            await CheckNumActivateDeactivateCalls(1, 0, activation, "After activation");
        }

        /// <summary>
        /// Tests deactivation with tail call optimization patterns.
        /// Validates proper lifecycle management for grains using tail call patterns.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Deactivate_TailCall()
        {
            int id = Random.Shared.Next();
            ITailCallActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<ITailCallActivateDeactivateTestGrain>(id);

            // Activate
            string activation = await grain.DoSomething();

            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation, "After deactivation");
        }

        /// <summary>
        /// Tests reactivation for grains using tail call patterns.
        /// Validates that tail call grains can be properly deactivated and reactivated.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Reactivate_TailCall()
        {
            int id = Random.Shared.Next();
            ITailCallActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<ITailCallActivateDeactivateTestGrain>(id);

            // Activate
            string activation = await grain.DoSomething();
            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation, "After deactivation");

            // Reactivate
            string activation2 = await grain.DoSomething();

            Assert.NotEqual(activation, activation2); // New activation created after re-activate
            await CheckNumActivateDeactivateCalls(2, 1, new[] { activation, activation2 }, "After reactivation");
        }

        /// <summary>
        /// Tests deactivation of grains with long-running operations.
        /// Validates that grains with reentrant long-running tasks can be properly deactivated
        /// and that a new activation is created on subsequent access.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("Reentrancy")]
        public async Task LongRunning_Deactivate()
        {
            int id = Random.Shared.Next();
            ILongRunningActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<ILongRunningActivateDeactivateTestGrain>(id);

            // Activate
            string activation = await grain.DoSomething();

            await CheckNumActivateDeactivateCalls(1, 0, activation, "Before deactivation");

            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation, "After deactivation");

            // Reactivate
            string activation2 = await grain.DoSomething();

            Assert.NotEqual(activation, activation2); // New activation created after re-activate;

            await CheckNumActivateDeactivateCalls(2, 1, new[] { activation, activation2 }, "After reactivation");
        }

        /// <summary>
        /// Tests handling of exceptions thrown during grain activation.
        /// Validates that exceptions in OnActivateAsync are properly propagated to the caller
        /// and that the grain fails to activate.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task BadActivate_Await()
        {
            try
            {
                int id = Random.Shared.Next();
                IBadActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id);

                await grain.ThrowSomething();

                Assert.Fail("Expected ThrowSomething call to fail as unable to Activate grain");
            }
            catch (ApplicationException exc)
            {
                Assert.Contains("Application-OnActivateAsync", exc.Message);
            }
        }

        /// <summary>
        /// Tests that grain methods fail when activation throws an exception.
        /// Validates that all grain method calls fail appropriately when OnActivateAsync fails.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task BadActivate_GetValue()
        {
            try
            {
                int id = Random.Shared.Next();
                IBadActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id);

                long key = await grain.GetKey();

                Assert.Fail("Expected ThrowSomething call to fail as unable to Activate grain, but returned " + key);
            }
            catch (ApplicationException exc)
            {
                Assert.Contains("Application-OnActivateAsync", exc.Message);
            }
        }

        /// <summary>
        /// Tests activation failure propagation through grain-to-grain calls.
        /// Validates that activation exceptions are properly propagated when a grain
        /// is activated indirectly through another grain's method call.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task BadActivate_Await_ViaOtherGrain()
        {
            try
            {
                int id = Random.Shared.Next();
                ICreateGrainReferenceTestGrain grain = this.GrainFactory.GetGrain<ICreateGrainReferenceTestGrain>(id);

                await grain.ForwardCall(this.GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id));

                Assert.Fail("Expected ThrowSomething call to fail as unable to Activate grain");
            }
            catch (ApplicationException exc)
            {
                Assert.Contains("Application-OnActivateAsync", exc.Message);
            }
        }

        /// <summary>
        /// Tests handling of exceptions thrown in grain constructors.
        /// Validates that constructor failures are properly handled and don't result in
        /// invalid operation exceptions, ensuring clean failure semantics.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Constructor_Bad_Await()
        {
            try
            {
                int id = Random.Shared.Next();
                IBadConstructorTestGrain grain = this.GrainFactory.GetGrain<IBadConstructorTestGrain>(id);

                await grain.DoSomething();

                Assert.Fail("Expected ThrowSomething call to fail as unable to Activate grain");
            }
            catch (TimeoutException te)
            {
                Console.WriteLine("Received timeout: " + te);
                throw; // Fail test
            }
            catch (Exception exc)
            {
                AssertIsNotInvalidOperationException(exc, "Constructor");
            }
        }

        /// <summary>
        /// Tests that grains can create grain references in their constructors.
        /// Validates that grain reference creation is allowed during grain construction phase.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Constructor_CreateGrainReference()
        {
            int id = Random.Shared.Next();
            ICreateGrainReferenceTestGrain grain = this.GrainFactory.GetGrain<ICreateGrainReferenceTestGrain>(id);

            string activation = await grain.DoSomething();
            Assert.NotNull(activation);
        }

        /// <summary>
        /// Tests deactivation of grains using Task-based activation patterns.
        /// Validates proper lifecycle management for grains with Task Action activation logic.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task TaskAction_Deactivate()
        {
            int id = Random.Shared.Next();
            ITaskActionActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<ITaskActionActivateDeactivateTestGrain>(id);

            // Activate
            string activation = await grain.DoSomething();

            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation.ToString());
        }

        /// <summary>
        /// Tests the scenario where a grain attempts to deactivate during activation.
        /// Validates that Orleans properly handles and rejects this invalid state transition,
        /// preventing race conditions in the activation lifecycle.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task DeactivateOnIdleWhileActivate()
        {
            int id = Random.Shared.Next();
            IDeactivatingWhileActivatingTestGrain grain = this.GrainFactory.GetGrain<IDeactivatingWhileActivatingTestGrain>(id);

            try
            {
                string activation = await grain.DoSomething();
                Assert.Fail("Should have thrown.");
            }
            catch (OrleansMessageRejectionException exc)
            {
                this.Logger.LogInformation(exc, "Thrown as expected");
                Assert.True(
                    exc.Message.Contains("Forwarding failed"),
                    "Did not get expected exception message returned: " + exc.Message);
            }  
        }

        private async Task CheckNumActivateDeactivateCalls(
            int expectedActivateCalls,
            int expectedDeactivateCalls,
            string forActivation,
            string when = null)
        {
            await CheckNumActivateDeactivateCalls(
                expectedActivateCalls,
                expectedDeactivateCalls,
                new string[] { forActivation },
                when )
            ;
        }

        private async Task CheckNumActivateDeactivateCalls(
            int expectedActivateCalls, 
            int expectedDeactivateCalls,
            string[] forActivations,
            string when = null)
        {
            string[] activateCalls = await watcher.GetActivateCalls();
            Assert.Equal(expectedActivateCalls, activateCalls.Length);

            string[] deactivateCalls = await watcher.GetDeactivateCalls();
            Assert.Equal(expectedDeactivateCalls, deactivateCalls.Length);

            for (int i = 0; i < expectedActivateCalls; i++)
            {
                Assert.Equal(forActivations[i], activateCalls[i]);
            }

            for (int i = 0; i < expectedDeactivateCalls; i++)
            {
                Assert.Equal(forActivations[i], deactivateCalls[i]);
            }
        }

        private static void AssertIsNotInvalidOperationException(Exception thrownException, string expectedMessageSubstring)
        {
            Console.WriteLine("Received exception: " + thrownException);
            Exception e = thrownException.GetBaseException();
            Console.WriteLine("Nested exception type: " + e.GetType().FullName);
            Console.WriteLine("Nested exception message: " + e.Message);

            Assert.IsAssignableFrom<Exception>(e);
            Assert.False(e is InvalidOperationException);
            Assert.True(e.Message.Contains(expectedMessageSubstring), "Did not get expected exception message returned: " + e.Message);

        }
    }
}
