using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.TestHelper;
using Xunit;
using Xunit.Abstractions;
using System.Linq;

namespace UnitTests.ActivationsLifeCycleTests
{
    [TestCategory("ActivationCollector")]
    public class DeactivateOnIdleTests : OrleansTestingBase, IDisposable
    {
        private readonly ITestOutputHelper output;
        private TestCluster testCluster;

        public DeactivateOnIdleTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private void Initialize(TestClusterBuilder builder = null)
        {
            if (builder == null)
            {
                builder = new TestClusterBuilder(1);
            }

            builder.ConfigureLegacyConfiguration();
            testCluster = builder.Build();
            testCluster.Deploy();
        }
        
        public void Dispose()
        {
            testCluster?.StopAllSilos();
            testCluster = null;
        }

        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdleTestInside_Basic()
        {
            Initialize();

            var a = this.testCluster.GrainFactory.GetGrain<ICollectionTestGrain>(1);
            var b = this.testCluster.GrainFactory.GetGrain<ICollectionTestGrain>(2);
            await a.SetOther(b);
            await a.GetOtherAge(); // prime a's routing cache
            await b.DeactivateSelf();
            Thread.Sleep(5000);
            var age = a.GetOtherAge().WaitForResultWithThrow(TimeSpan.FromMilliseconds(2000));
            Assert.True(age.TotalMilliseconds < 2000, "Should be newly activated grain");
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Functional")]
        public async Task DeactivateOnIdleTest_Stress_1()
        {
            Initialize();

            var a = this.testCluster.GrainFactory.GetGrain<ICollectionTestGrain>(1);
            await a.GetAge();
            await a.DeactivateSelf();
            for (int i = 0; i < 30; i++)
            {
                await a.GetAge();
            }
        }

        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdleTest_Stress_2_NonReentrant()
        {
            Initialize();
            var a = this.testCluster.GrainFactory.GetGrain<ICollectionTestGrain>(1, "UnitTests.Grains.CollectionTestGrain");
            await a.IncrCounter();

            Task t1 = Task.Run(async () =>
            {
                List<Task> tasks = new List<Task>();
                for (int i = 0; i < 100; i++)
                {
                    tasks.Add(a.IncrCounter());
                }
                await Task.WhenAll(tasks);
            });

            await Task.Delay(1);
            Task t2 = a.DeactivateSelf();
            await Task.WhenAll(t1, t2);
        }

        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdleTest_Stress_3_Reentrant()
        {
            Initialize();
            var a = this.testCluster.GrainFactory.GetGrain<ICollectionTestGrain>(1, "UnitTests.Grains.ReentrantCollectionTestGrain");
            await a.IncrCounter();

            Task t1 = Task.Run(async () =>
            {
                List<Task> tasks = new List<Task>();
                for (int i = 0; i < 100; i++)
                {
                    tasks.Add(a.IncrCounter());
                }
                await Task.WhenAll(tasks);
            });

            await Task.Delay(TimeSpan.FromMilliseconds(1));
            Task t2 = a.DeactivateSelf();
            await Task.WhenAll(t1, t2);
        }

        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdleTest_Stress_4_Timer()
        {
            Initialize();
            var a = this.testCluster.GrainFactory.GetGrain<ICollectionTestGrain>(1, "UnitTests.Grains.ReentrantCollectionTestGrain");
            for (int i = 0; i < 10; i++)
            {
                await a.StartTimer(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(100));
            }
            await a.DeactivateSelf();
            await a.IncrCounter();
        }

        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdleTest_Stress_5()
        {
            Initialize();
            var a = this.testCluster.GrainFactory.GetGrain<ICollectionTestGrain>(1);
            await a.IncrCounter();

            Task t1 = Task.Run(async () =>
            {
                List<Task> tasks = new List<Task>();
                for (int i = 0; i < 100; i++)
                {
                    tasks.Add(a.IncrCounter());
                }
                await Task.WhenAll(tasks);
            });
            Task t2 = Task.Run(async () =>
            {
                List<Task> tasks = new List<Task>();
                for (int i = 0; i < 1; i++)
                {
                    await Task.Delay(1);
                    tasks.Add(a.DeactivateSelf());
                }
                await Task.WhenAll(tasks);
            });
            await Task.WhenAll(t1, t2);
        }

        [Fact, TestCategory("Stress")]
        public async Task DeactivateOnIdleTest_Stress_11()
        {
            Initialize();
            var a = this.testCluster.GrainFactory.GetGrain<ICollectionTestGrain>(1);
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < 100; i++)
            {
                tasks.Add(a.IncrCounter());
            }
            await Task.WhenAll(tasks);
        }

        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdle_NonExistentActivation_1()
        {
            await DeactivateOnIdle_NonExistentActivation_Runner(0);
        }

        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdle_NonExistentActivation_2()
        {
            await DeactivateOnIdle_NonExistentActivation_Runner(1);
        }

        private async Task DeactivateOnIdle_NonExistentActivation_Runner(int forwardCount)
        {
            var builder = new TestClusterBuilder(2);
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.Globals.MaxForwardCount = forwardCount;
                // For this test we only want to talk to the primary
                legacy.ClientConfiguration.Gateways.RemoveAt(1);
            });
            Initialize(builder);

            ICollectionTestGrain grain = await PickGrainInNonPrimary();

            output.WriteLine("About to make a 1st GetAge() call.");
            TimeSpan age = await grain.GetAge();
            output.WriteLine(age.ToString());

            await grain.DeactivateSelf();
            await Task.Delay(3000);

            // ReSharper disable once PossibleNullReferenceException
            var thrownException = await Record.ExceptionAsync(() => grain.GetAge());
            if (forwardCount != 0)
            {
                Assert.Null(thrownException);
                output.WriteLine("\nThe 1st call after DeactivateSelf has NOT thrown any exception as expected, since forwardCount is {0}.\n", forwardCount);
            }
            else
            {
                Assert.NotNull(thrownException);
                Assert.IsType<OrleansMessageRejectionException>(thrownException);
                Assert.Contains("Non-existent activation", thrownException.Message);
                output.WriteLine("\nThe 1st call after DeactivateSelf has thrown Non-existent activation exception as expected, since forwardCount is {0}.\n", forwardCount);

                // Try sending agan now and see it was fixed.
                await grain.GetAge();
            }
        }

        private async Task<ICollectionTestGrain> PickGrainInNonPrimary()
        {
            for (int i = 0; i < 500; i++)
            {
                if (i % 30 == 29) await Task.Delay(1000); // give some extra time to stabilize if it can't find a suitable grain

                // Create grain such that:
                // Its directory owner is not the Gateway silo. This way Gateway will use its directory cache.
                // Its activation is located on the non Gateway silo as well.
                ICollectionTestGrain grain = this.testCluster.GrainFactory.GetGrain<ICollectionTestGrain>(i);
                GrainId grainId = ((GrainReference)await grain.GetGrainReference()).GrainId;
                SiloAddress primaryForGrain = (await TestUtils.GetDetailedGrainReport(this.testCluster.InternalGrainFactory, grainId, this.testCluster.Primary)).PrimaryForGrain;
                if (primaryForGrain.Equals(this.testCluster.Primary.SiloAddress))
                {
                    continue;
                }
                string siloHostingActivation = await grain.GetRuntimeInstanceId();
                if (this.testCluster.Primary.SiloAddress.ToLongString().Equals(siloHostingActivation))
                {
                    continue;
                }
                this.output.WriteLine("\nCreated grain with key {0} whose primary directory owner is silo {1} and which was activated on silo {2}\n", i, primaryForGrain.ToLongString(), siloHostingActivation);
                return grain;
            }

            Assert.True(testCluster.GetActiveSilos().Count() > 1, "This logic requires at least 1 non-primary active silo");
            Assert.True(false, "Could not find a grain that activates on a non-primary silo, and has the partition be also managed by a non-primary silo");
            return null;
        }

        [Fact, TestCategory("Functional")]
        public async Task MissingActivation_WithoutDirectoryLazyDeregistration_MultiSilo()
        {
            var directoryLazyDeregistrationDelay = TimeSpan.FromMilliseconds(-1);
            var builder = new TestClusterBuilder(2);
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                // Disable retries in this case, to make test more predictable.
                legacy.ClusterConfiguration.Globals.MaxForwardCount = 0;
                legacy.ClientConfiguration.Gateways.RemoveAt(1);
            });
            Initialize(builder);
            for (int i = 0; i < 10; i++)
            {
                await MissingActivation_Runner(i, directoryLazyDeregistrationDelay);
            }
        }

        [Fact, TestCategory("Functional")]
        public async Task MissingActivation_WithDirectoryLazyDeregistration_SingleSilo()
        {
            var directoryLazyDeregistrationDelay = TimeSpan.FromMilliseconds(5000);
            var lazyDeregistrationDelay = TimeSpan.FromMilliseconds(5000);
            var builder = new TestClusterBuilder(1);
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                legacy.ClusterConfiguration.Globals.DirectoryLazyDeregistrationDelay = directoryLazyDeregistrationDelay;
                // Disable retries in this case, to make test more predictable.
                legacy.ClusterConfiguration.Globals.MaxForwardCount = 0;
            });

            Initialize(builder);

            for (int i = 0; i < 10; i++)
            {
                await MissingActivation_Runner(i, lazyDeregistrationDelay);
            }
        }

        [Fact(Skip = "Needs investigation"), TestCategory("Functional")]
        public async Task MissingActivation_WithoutDirectoryLazyDeregistration_MultiSilo_SecondaryFirst()
        {
            var lazyDeregistrationDelay = TimeSpan.FromMilliseconds(-1);
            var builder = new TestClusterBuilder(2);
            builder.ConfigureLegacyConfiguration(legacy =>
            {
                // Disable retries in this case, to make test more predictable.
                legacy.ClusterConfiguration.Globals.MaxForwardCount = 0;
                legacy.ClientConfiguration.Gateways.RemoveAt(1);
            });

            Initialize(builder);

            await MissingActivation_Runner(1, lazyDeregistrationDelay, true);
        }

        private async Task MissingActivation_Runner(
            int grainId,
            TimeSpan lazyDeregistrationDelay,
            bool forceCreationInSecondary = false)
        {
            output.WriteLine("\n\n\n SMissingActivation_Runner.\n\n\n");

            var realGrainId = grainId;

            ITestGrain grain;

            var isMultipleSilosPresent = testCluster.SecondarySilos != null && testCluster.SecondarySilos.Count > 0;

            if (!isMultipleSilosPresent && forceCreationInSecondary)
            {
                throw new InvalidOperationException(
                          "If 'forceCreationInSecondary' is true multiple silos must be present, check the test!");
            }

            var grainSiloAddress = String.Empty;
            var primarySiloAddress = testCluster.Primary.SiloAddress.ToString();
            var secondarySiloAddress = isMultipleSilosPresent
                                           ? testCluster.SecondarySilos[0].SiloAddress.ToString()
                                           : String.Empty;

            //
            // We only doing this for multiple silos.
            //

            if (isMultipleSilosPresent && forceCreationInSecondary)
            {
                //
                // Make sure that we proceeding with a grain which is created in the secondary silo for first!
                //

                while (true)
                {
                    this.output.WriteLine($"GetGrain: {realGrainId}");

                    grain = this.testCluster.GrainFactory.GetGrain<ITestGrain>(realGrainId);

                    grainSiloAddress = await grain.GetRuntimeInstanceId();

                    if (grainSiloAddress != secondarySiloAddress)
                    {
                        this.output.WriteLine($"GetGrain: {realGrainId} Primary, skipping.");

                        realGrainId++;
                    }
                    else
                    {
                        this.output.WriteLine($"GetGrain: {realGrainId} Secondary, proceeding.");

                        break;
                    }
                }
            }
            else
            {
                grain = this.testCluster.GrainFactory.GetGrain<ITestGrain>(realGrainId);
            }

            await grain.SetLabel("hello_" + grainId);
            var grainReference = ((GrainReference)await grain.GetGrainReference()).GrainId;

            // Call again to make sure the grain is in all silo caches
            for (int i = 0; i < 10; i++)
            {
                var label = await grain.GetLabel();
            }

            // Now we know that there's an activation; try both silos and deactivate it incorrectly
            int primaryActivation =
                await
                    this.testCluster.Client.GetTestHooks(testCluster.Primary)
                        .UnregisterGrainForTesting(grainReference);
            int secondaryActivation = 0;

            if (isMultipleSilosPresent)
            {
                secondaryActivation =
                    await
                        this.testCluster.Client.GetTestHooks(testCluster.SecondarySilos[0])
                            .UnregisterGrainForTesting(grainReference);
            }

            Assert.Equal(1, primaryActivation + secondaryActivation);

            // If we try again, we shouldn't find any
            primaryActivation =
                await
                    this.testCluster.Client.GetTestHooks(testCluster.Primary)
                        .UnregisterGrainForTesting(grainReference);
            secondaryActivation = 0;

            if (isMultipleSilosPresent)
            {
                secondaryActivation =
                    await
                        this.testCluster.Client.GetTestHooks(testCluster.SecondarySilos[0])
                            .UnregisterGrainForTesting(grainReference);
            }

            Assert.Equal(0, primaryActivation + secondaryActivation);

            if (lazyDeregistrationDelay > TimeSpan.Zero)
            {
                // Wait a bit
                TimeSpan pause = lazyDeregistrationDelay.Multiply(2);
                output.WriteLine($"Pausing for {0} because we are using lazy deregistration", pause);
                await Task.Delay(pause);
            }

            // Now send a message again; it should fail);
            var firstEx = await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => grain.GetLabel());
            Assert.Contains("Non-existent activation", firstEx.Message);
            output.WriteLine("Got 1st Non-existent activation Exception, as expected.");

            // Try again; it should succeed or fail, based on doLazyDeregistration
            if (lazyDeregistrationDelay > TimeSpan.Zero || forceCreationInSecondary)
            {
                var newLabel = "";

                newLabel = await grain.GetLabel();

                // Since a new instance returned, we've to check that the label is no more prefixed with "hello_"
                Assert.Equal(grainId.ToString(), newLabel);

                output.WriteLine($"After 2nd call. newLabel = '{newLabel}'");

                if (forceCreationInSecondary)
                {
                    grainSiloAddress = await grain.GetRuntimeInstanceId();

                    output.WriteLine(
                        grainSiloAddress == primarySiloAddress ? "Recreated in Primary" : "Recreated in Secondary");
                    output.WriteLine(
                        grainSiloAddress == primarySiloAddress ? "Recreated in Primary" : "Recreated in Secondary");
                }
            }
            else
            {
                var secondEx = await Assert.ThrowsAsync<OrleansMessageRejectionException>(() => grain.GetLabel());
                output.WriteLine("Got 2nd exception - " + secondEx.GetBaseException().Message);
                Assert.True(
                    secondEx.Message.Contains("duplicate activation")
                    || secondEx.Message.Contains("Non-existent activation")
                    || secondEx.Message.Contains("Forwarding failed"),
                    "2nd exception message: " + secondEx);
                output.WriteLine("Got 2nd exception, as expected.");
            }
        }
    }
}
