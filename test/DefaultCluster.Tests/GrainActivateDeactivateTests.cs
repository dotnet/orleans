using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime;
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
            watcher = this.GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);
            watcher.Clear().Wait();
        }

        public virtual void Dispose()
        {
            watcher.Clear().Wait();
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public async Task WatcherGrain_GetGrain()
        {
            IActivateDeactivateWatcherGrain grain = this.GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(1);
            await grain.Clear();
        }

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Activate_Simple()
        {
            int id = Random.Shared.Next();
            ISimpleActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<ISimpleActivateDeactivateTestGrain>(id);

            string activation = await grain.DoSomething();

            await CheckNumActivateDeactivateCalls(1, 0, activation, "After activation");
        }

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

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task Activate_TailCall()
        {
            int id = Random.Shared.Next();
            ITailCallActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<ITailCallActivateDeactivateTestGrain>(id);

            string activation = await grain.DoSomething();

            await CheckNumActivateDeactivateCalls(1, 0, activation, "After activation");
        }

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

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task BadActivate_Await()
        {
            try
            {
                int id = Random.Shared.Next();
                IBadActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id);

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
                int id = Random.Shared.Next();
                IBadActivateDeactivateTestGrain grain = this.GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id);

                long key = await grain.GetKey();

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
                int id = Random.Shared.Next();
                ICreateGrainReferenceTestGrain grain = this.GrainFactory.GetGrain<ICreateGrainReferenceTestGrain>(id);

                await grain.ForwardCall(this.GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id));

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
                int id = Random.Shared.Next();
                IBadConstructorTestGrain grain = this.GrainFactory.GetGrain<IBadConstructorTestGrain>(id);

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
            int id = Random.Shared.Next();
            ICreateGrainReferenceTestGrain grain = this.GrainFactory.GetGrain<ICreateGrainReferenceTestGrain>(id);

            string activation = await grain.DoSomething();
            Assert.NotNull(activation);
        }

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

        [Fact, TestCategory("BVT"), TestCategory("ActivateDeactivate")]
        public async Task DeactivateOnIdleWhileActivate()
        {
            int id = Random.Shared.Next();
            IDeactivatingWhileActivatingTestGrain grain = this.GrainFactory.GetGrain<IDeactivatingWhileActivatingTestGrain>(id);

            try
            {
                string activation = await grain.DoSomething();
                Assert.True(false, "Should have thrown.");
            }
            catch(InvalidOperationException exc)
            {
                this.Logger.LogInformation(exc, "Thrown as expected");
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
