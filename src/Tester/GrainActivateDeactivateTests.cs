using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

namespace UnitTests.ActivationsLifeCycleTests
{
    [TestClass]
    public class GrainActivateDeactivateTests : UnitTestSiloHost
    {
        private IActivateDeactivateWatcherGrain watcher;

        public GrainActivateDeactivateTests()
            : base(new TestingSiloOptions { StartFreshOrleans = true, StartSecondary = false }) // Only need single silo
        {
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            StopAllSilos();
        }

        [TestInitialize]
        public void TestInitialize()
        {
            watcher = GrainClient.GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(0);
            watcher.Clear().Wait();
        }

        [TestCleanup]
        public void TestCleanup()
        {
            if (watcher != null)
            {
                watcher.Clear().Wait();
                watcher = null;
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate"), TestCategory("GetGrain")]
        public async Task WatcherGrain_GetGrain()
        {
            IActivateDeactivateWatcherGrain grain = GrainClient.GrainFactory.GetGrain<IActivateDeactivateWatcherGrain>(1);
            await grain.Clear();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task Activate_Simple()
        {
            int id = random.Next();
            ISimpleActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<ISimpleActivateDeactivateTestGrain>(id);

            string activation = await grain.DoSomething();

            await CheckNumActivateDeactivateCalls(1, 0, activation, "After activation");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
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

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
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

            Assert.AreNotEqual(activation, activation2, "New activation created after re-activate");
            await CheckNumActivateDeactivateCalls(2, 1, new[] { activation, activation2 }, "After reactivation");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task Activate_TailCall()
        {
            int id = random.Next();
            ITailCallActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<ITailCallActivateDeactivateTestGrain>(id);

            string activation = await grain.DoSomething();

            await CheckNumActivateDeactivateCalls(1, 0, activation, "After activation");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
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

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
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

            Assert.AreNotEqual(activation, activation2, "New activation created after re-activate");
            await CheckNumActivateDeactivateCalls(2, 1, new[] { activation, activation2 }, "After reactivation");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate"), TestCategory("Reentrancy")]
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

            Assert.AreNotEqual(activation, activation2, "New activation created after re-activate");

            await CheckNumActivateDeactivateCalls(2, 1, new[] { activation, activation2 }, "After reactivation");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task BadActivate_Await()
        {
            try
            {
                int id = random.Next();
                IBadActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id);

                await grain.ThrowSomething();

                Assert.Fail("Expected ThrowSomething call to fail as unable to Activate grain");
            }
            catch (Exception exc)
            {
                Console.WriteLine("Received exception: " + exc);
                Exception e = exc.GetBaseException();
                Console.WriteLine("Nested exception type: " + e.GetType().FullName);
                Console.WriteLine("Nested exception message: " + e.Message);
                Assert.IsInstanceOfType(e, typeof(Exception), "Did not get expected exception type returned: " + e);
                Assert.IsNotInstanceOfType(e, typeof(InvalidOperationException), "Did not get expected exception type returned: " + e);
                Assert.IsTrue(e.Message.Contains("Application-OnActivateAsync"), "Did not get expected exception message returned: " + e.Message);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task BadActivate_GetValue()
        {
            try
            {
                int id = random.Next();
                IBadActivateDeactivateTestGrain grain = GrainClient.GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id);

                long key = await grain.GetKey();

                Assert.Fail("Expected ThrowSomething call to fail as unable to Activate grain, but returned " + key);
            }
            catch (Exception exc)
            {
                Console.WriteLine("Received exception: " + exc);
                Exception e = exc.GetBaseException();
                Console.WriteLine("Nested exception type: " + e.GetType().FullName);
                Console.WriteLine("Nested exception message: " + e.Message);
                Assert.IsInstanceOfType(e, typeof(Exception), "Did not get expected exception type returned: " + e);
                Assert.IsNotInstanceOfType(e, typeof(InvalidOperationException), "Did not get expected exception type returned: " + e);
                Assert.IsTrue(e.Message.Contains("Application-OnActivateAsync"), "Did not get expected exception message returned: " + e.Message);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task BadActivate_Await_ViaOtherGrain()
        {
            try
            {
                int id = random.Next();
                ICreateGrainReferenceTestGrain grain = GrainClient.GrainFactory.GetGrain<ICreateGrainReferenceTestGrain>(id);

                await grain.ForwardCall(GrainClient.GrainFactory.GetGrain<IBadActivateDeactivateTestGrain>(id));

                Assert.Fail("Expected ThrowSomething call to fail as unable to Activate grain");
            }
            catch (Exception exc)
            {
                Console.WriteLine("Received exception: " + exc);
                Exception e = exc.GetBaseException();
                Console.WriteLine("Nested exception type: " + e.GetType().FullName);
                Console.WriteLine("Nested exception message: " + e.Message);
                Assert.IsInstanceOfType(e, typeof(Exception), "Did not get expected exception type returned: " + e);
                Assert.IsNotInstanceOfType(e, typeof(InvalidOperationException), "Did not get expected exception type returned: " + e);
                Assert.IsTrue(e.Message.Contains("Application-OnActivateAsync"), "Did not get expected exception message returned: " + e.Message);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task Constructor_Bad_Await()
        {
            try
            {
                int id = random.Next();
                IBadConstructorTestGrain grain = GrainClient.GrainFactory.GetGrain<IBadConstructorTestGrain>(id);

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
                Console.WriteLine("Received exception: " + exc);
                Exception e = exc.GetBaseException();
                Console.WriteLine("Nested exception type: " + e.GetType().FullName);
                Console.WriteLine("Nested exception message: " + e.Message);
                Assert.IsInstanceOfType(e, typeof(Exception),
                                        "Did not get expected exception type returned: " + e);
                Assert.IsNotInstanceOfType(e, typeof(InvalidOperationException),
                                           "Did not get expected exception type returned: " + e);
                Assert.IsTrue(e.Message.Contains("Constructor"),
                              "Did not get expected exception message returned: " + e.Message);
            }
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
        public async Task Constructor_CreateGrainReference()
        {
            int id = random.Next();
            ICreateGrainReferenceTestGrain grain = GrainClient.GrainFactory.GetGrain<ICreateGrainReferenceTestGrain>(id);

            string activation = await grain.DoSomething();
            Assert.IsNotNull(activation);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("ActivateDeactivate")]
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
            Assert.AreEqual(expectedActivateCalls, activateCalls.Length, "Number of Activate calls {0}", when);

            string[] deactivateCalls = await watcher.GetDeactivateCalls();
            Assert.AreEqual(expectedDeactivateCalls, deactivateCalls.Length, "Number of Deactivate calls {0}", when);

            for (int i = 0; i < expectedActivateCalls; i++)
            {
                Assert.AreEqual(forActivations[i], activateCalls[i], "Activate call #{0} was by expected activation {1}", (i+1), when);
            }

            for (int i = 0; i < expectedDeactivateCalls; i++)
            {
                Assert.AreEqual(forActivations[i], deactivateCalls[i], "Deactivate call #{0} was by expected activation {1}", (i + 1), when);
            }
        }
    }
}
