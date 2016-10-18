using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests
{
    public class GrainActivateDeactivateTests : HostedTestClusterEnsureDefaultStarted, IDisposable
    {
        private IActivateDeactivateWatcherGrain watcher;

        public GrainActivateDeactivateTests()
        {
            watcher = GrainClient.GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);
            watcher.Clear().Wait();
        }

        public void Dispose()
        {
            watcher.Clear().Wait();
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public async Task WatcherGrain_GetGrain()
        {
            IActivateDeactivateWatcherGrain grain = GrainClient.GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(1);
            await grain.Clear();
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task Activate_Simple()
        {
            int id = random.Next();
            ISimpleActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<ISimpleActivateDeactivateTestGrain>(id);

            string activation = await grain.DoSomething();

            await CheckNumActivateDeactivateCalls(1, 0, activation, "After activation");
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task Deactivate_Simple()
        {
            int id = random.Next();
            ISimpleActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<ISimpleActivateDeactivateTestGrain>(id);

            // Activate
            string activation = await grain.DoSomething();

            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation, "After deactivation");
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task Reactivate_Simple()
        {
            int id = random.Next();
            ISimpleActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<ISimpleActivateDeactivateTestGrain>(id);

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

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task Activate_TailCall()
        {
            int id = random.Next();
            ITailCallActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<ITailCallActivateDeactivateTestGrain>(id);

            string activation = await grain.DoSomething();

            await CheckNumActivateDeactivateCalls(1, 0, activation, "After activation");
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task Deactivate_TailCall()
        {
            int id = random.Next();
            ITailCallActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<ITailCallActivateDeactivateTestGrain>(id);

            // Activate
            string activation = await grain.DoSomething();

            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation, "After deactivation");
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task Reactivate_TailCall()
        {
            int id = random.Next();
            ITailCallActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<ITailCallActivateDeactivateTestGrain>(id);

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

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate"), TestCategory("Reentrancy")]
        public async Task LongRunning_Deactivate()
        {
            int id = random.Next();
            ILongRunningActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<ILongRunningActivateDeactivateTestGrain>(id);

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

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task BadActivate_Await()
        {
            try
            {
                int id = random.Next();
                IBadActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id);

                await grain.ThrowSomething();

                Assert.True(false, "Expected ThrowSomething call to fail as unable to Activate grain");
            }
            catch (Exception exc)
            {
                AssertIsNotInvalidOperationException(exc, "Application-OnActivateAsync");
            }
            
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task BadActivate_GetValue()
        {
            try
            {
                int id = random.Next();
                IBadActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id);

                long key = await grain.GetKey();

                Assert.True(false, "Expected ThrowSomething call to fail as unable to Activate grain, but returned " + key);
            }
            catch (Exception exc)
            {
                AssertIsNotInvalidOperationException(exc, "Application-OnActivateAsync");
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task BadActivate_Await_ViaOtherGrain()
        {
            try
            {
                int id = random.Next();
                ICreateGrainReferenceTestGrain grain = GrainClient.GrainFactory.GetGrain<ICreateGrainReferenceTestGrain>(id);

                await grain.ForwardCall(GrainClient.GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id));

                Assert.True(false, "Expected ThrowSomething call to fail as unable to Activate grain");
            }
            catch (Exception exc)
            {
                AssertIsNotInvalidOperationException(exc, "Application-OnActivateAsync");
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task Constructor_Bad_Await()
        {
            try
            {
                int id = random.Next();
                IBadConstructorTestGrain grain = GrainClient.GrainFactory.GetGrain<IBadConstructorTestGrain>(id);

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

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task Constructor_CreateGrainReference()
        {
            int id = random.Next();
            ICreateGrainReferenceTestGrain grain = GrainClient.GrainFactory.GetGrain<ICreateGrainReferenceTestGrain>(id);

            string activation = await grain.DoSomething();
            Assert.NotNull(activation);
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task TaskAction_Deactivate()
        {
            int id = random.Next();
            ITaskActionActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<ITaskActionActivateDeactivateTestGrain>(id);

            // Activate
            string activation = await grain.DoSomething();

            // Deactivate
            await grain.DoDeactivate();
            Thread.Sleep(TimeSpan.FromSeconds(2)); // Allow some time for deactivate to happen

            await CheckNumActivateDeactivateCalls(1, 1, activation.ToString());
        }

        [Fact, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task DeactivateOnIdleWhileActivate()
        {
            int id = random.Next();
            IDeactivatingWhileActivatingTestGrain grain = GrainClient.GrainFactory.GetGrain<IDeactivatingWhileActivatingTestGrain>(id);

            try
            {
                string activation = await grain.DoSomething();
                Assert.True(false, "Should have thrown.");
            }
            catch(Exception exc)
            {
                logger.Info("Thrown as expected:", exc);
                Exception e = exc.GetBaseException();
                Assert.True(e.Message.Contains("Forwarding failed"),
                        "Did not get expected exception message returned: " + e.Message);
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
