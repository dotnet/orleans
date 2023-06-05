using Microsoft.Extensions.Logging;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.ActivationsLifeCycleTests
{
    public class GrainActivateDeactivateTests : HostedTestClusterEnsureDefaultStarted, IDisposable
    {
        private IActivateDeactivateWatcherGrain watcher;

        public GrainActivateDeactivateTests(DefaultClusterFixture fixture) : base(fixture)
        {
            watcher = GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);
            watcher.Clear().Wait();
        }

        public virtual void Dispose() => watcher.Clear().Wait();

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public async Task WatcherGrain_GetGrain()
        {
            var grain = GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(1);
            await grain.Clear();
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Activate_Simple()
        {
            var id = Random.Shared.Next();
            var grain = GrainFactory.GetGrain<ISimpleActivateDeactivateTestGrain>(id);

            var activation = await grain.DoSomething();

            await CheckNumActivateDeactivateCalls(1, 0, activation, "After activation");
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Deactivate_Simple()
        {
            var id = Random.Shared.Next();
            var grain = GrainFactory.GetGrain<ISimpleActivateDeactivateTestGrain>(id);

            // Activate
            var activation = await grain.DoSomething();

            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation, "After deactivation");
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Reactivate_Simple()
        {
            var id = Random.Shared.Next();
            var grain = GrainFactory.GetGrain<ISimpleActivateDeactivateTestGrain>(id);

            // Activate
            var activation = await grain.DoSomething();
            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation, "After deactivation");

            // Reactivate
            var activation2 = await grain.DoSomething();

            Assert.NotEqual(activation, activation2); // New activation created after re-activate
            await CheckNumActivateDeactivateCalls(2, 1, new[] { activation, activation2 }, "After reactivation");
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Activate_TailCall()
        {
            var id = Random.Shared.Next();
            var grain = GrainFactory.GetGrain<ITailCallActivateDeactivateTestGrain>(id);

            var activation = await grain.DoSomething();

            await CheckNumActivateDeactivateCalls(1, 0, activation, "After activation");
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Deactivate_TailCall()
        {
            var id = Random.Shared.Next();
            var grain = GrainFactory.GetGrain<ITailCallActivateDeactivateTestGrain>(id);

            // Activate
            var activation = await grain.DoSomething();

            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation, "After deactivation");
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Reactivate_TailCall()
        {
            var id = Random.Shared.Next();
            var grain = GrainFactory.GetGrain<ITailCallActivateDeactivateTestGrain>(id);

            // Activate
            var activation = await grain.DoSomething();
            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation, "After deactivation");

            // Reactivate
            var activation2 = await grain.DoSomething();

            Assert.NotEqual(activation, activation2); // New activation created after re-activate
            await CheckNumActivateDeactivateCalls(2, 1, new[] { activation, activation2 }, "After reactivation");
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("Reentrancy")]
        public async Task LongRunning_Deactivate()
        {
            var id = Random.Shared.Next();
            var grain = GrainFactory.GetGrain<ILongRunningActivateDeactivateTestGrain>(id);

            // Activate
            var activation = await grain.DoSomething();

            await CheckNumActivateDeactivateCalls(1, 0, activation, "Before deactivation");

            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation, "After deactivation");

            // Reactivate
            var activation2 = await grain.DoSomething();

            Assert.NotEqual(activation, activation2); // New activation created after re-activate;

            await CheckNumActivateDeactivateCalls(2, 1, new[] { activation, activation2 }, "After reactivation");
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task BadActivate_Await()
        {
            try
            {
                var id = Random.Shared.Next();
                var grain = GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id);

                await grain.ThrowSomething();

                Assert.True(false, "Expected ThrowSomething call to fail as unable to Activate grain");
            }
            catch (ApplicationException exc)
            {
                Assert.Contains("Application-OnActivateAsync", exc.Message);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task BadActivate_GetValue()
        {
            try
            {
                var id = Random.Shared.Next();
                var grain = GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id);

                var key = await grain.GetKey();

                Assert.True(false, "Expected ThrowSomething call to fail as unable to Activate grain, but returned " + key);
            }
            catch (ApplicationException exc)
            {
                Assert.Contains("Application-OnActivateAsync", exc.Message);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task BadActivate_Await_ViaOtherGrain()
        {
            try
            {
                var id = Random.Shared.Next();
                var grain = GrainFactory.GetGrain<ICreateGrainReferenceTestGrain>(id);

                await grain.ForwardCall(GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id));

                Assert.True(false, "Expected ThrowSomething call to fail as unable to Activate grain");
            }
            catch (ApplicationException exc)
            {
                Assert.Contains("Application-OnActivateAsync", exc.Message);
            }
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Constructor_Bad_Await()
        {
            try
            {
                var id = Random.Shared.Next();
                var grain = GrainFactory.GetGrain<IBadConstructorTestGrain>(id);

                await grain.DoSomething();

                Assert.True(false, "Expected ThrowSomething call to fail as unable to Activate grain");
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

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Constructor_CreateGrainReference()
        {
            var id = Random.Shared.Next();
            var grain = GrainFactory.GetGrain<ICreateGrainReferenceTestGrain>(id);

            var activation = await grain.DoSomething();
            Assert.NotNull(activation);
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task TaskAction_Deactivate()
        {
            var id = Random.Shared.Next();
            var grain = GrainFactory.GetGrain<ITaskActionActivateDeactivateTestGrain>(id);

            // Activate
            var activation = await grain.DoSomething();

            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation.ToString());
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task DeactivateOnIdleWhileActivate()
        {
            var id = Random.Shared.Next();
            var grain = GrainFactory.GetGrain<IDeactivatingWhileActivatingTestGrain>(id);

            try
            {
                var activation = await grain.DoSomething();
                Assert.True(false, "Should have thrown.");
            }
            catch(InvalidOperationException exc)
            {
                Logger.LogInformation(exc, "Thrown as expected");
                Assert.True(
                    exc.Message.Contains("DeactivateOnIdle from within OnActivateAsync"),
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
            var activateCalls = await watcher.GetActivateCalls();
            Assert.Equal(expectedActivateCalls, activateCalls.Length);

            var deactivateCalls = await watcher.GetDeactivateCalls();
            Assert.Equal(expectedDeactivateCalls, deactivateCalls.Length);

            for (var i = 0; i < expectedActivateCalls; i++)
            {
                Assert.Equal(forActivations[i], activateCalls[i]);
            }

            for (var i = 0; i < expectedDeactivateCalls; i++)
            {
                Assert.Equal(forActivations[i], deactivateCalls[i]);
            }
        }

        private static void AssertIsNotInvalidOperationException(Exception thrownException, string expectedMessageSubstring)
        {
            Console.WriteLine("Received exception: " + thrownException);
            var e = thrownException.GetBaseException();
            Console.WriteLine("Nested exception type: " + e.GetType().FullName);
            Console.WriteLine("Nested exception message: " + e.Message);

            Assert.IsAssignableFrom<Exception>(e);
            Assert.False(e is InvalidOperationException);
            Assert.True(e.Message.Contains(expectedMessageSubstring), "Did not get expected exception message returned: " + e.Message);

        }
    }
}
