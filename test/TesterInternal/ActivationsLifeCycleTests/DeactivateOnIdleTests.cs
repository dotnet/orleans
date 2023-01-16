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
using Orleans.Internal;
using Orleans.Hosting;
using Orleans.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

            testCluster = builder.Build();
            testCluster.Deploy();
        }
        
        public void Dispose()
        {
            try
            {
                testCluster?.StopAllSilos();
            }
            finally
            {
                testCluster?.Dispose();
                testCluster = null;
            }
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

        [Fact, TestCategory("SlowBVT")]
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

        public class ClientConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.Configure<StaticGatewayListProviderOptions>(options => { options.Gateways = options.Gateways.Take(1).ToList(); });
            }
        }

        public class SiloConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                var cfg = hostBuilder.GetConfiguration();
                var maxForwardCount = int.Parse(cfg["MaxForwardCount"]);
                hostBuilder.ConfigureServices(services =>
                {
                    services.Configure<SiloMessagingOptions>(options => options.MaxForwardCount = maxForwardCount);
                });
            }
        }

        private async Task DeactivateOnIdle_NonExistentActivation_Runner(int forwardCount)
        {
            var builder = new TestClusterBuilder(2);
            builder.AddClientBuilderConfigurator<ClientConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            builder.Properties["MaxForwardCount"] = forwardCount.ToString();
            Initialize(builder);

            ICollectionTestGrain grain = await PickGrainInNonPrimary();

            output.WriteLine("About to make a 1st GetAge() call.");
            TimeSpan age = await grain.GetAge();
            output.WriteLine(age.ToString());

            await grain.DeactivateSelf();
            await Task.Delay(3000);

            var thrownException = await Record.ExceptionAsync(() => grain.GetAge());
            Assert.Null(thrownException);
            output.WriteLine("\nThe 1st call after DeactivateSelf has NOT thrown any exception as expected, since forwardCount is {0}.\n", forwardCount);
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
                if (this.testCluster.Primary.SiloAddress.ToString().Equals(siloHostingActivation))
                {
                    continue;
                }
                this.output.WriteLine("\nCreated grain with key {0} whose primary directory owner is silo {1} and which was activated on silo {2}\n", i, primaryForGrain.ToString(), siloHostingActivation);
                return grain;
            }

            Assert.True(testCluster.GetActiveSilos().Count() > 1, "This logic requires at least 1 non-primary active silo");
            Assert.True(false, "Could not find a grain that activates on a non-primary silo, and has the partition be also managed by a non-primary silo");
            return null;
        }
    }
}
