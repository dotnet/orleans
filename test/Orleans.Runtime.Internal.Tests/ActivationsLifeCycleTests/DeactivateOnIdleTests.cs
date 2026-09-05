using Orleans.Runtime;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Orleans.Internal;
using Orleans.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Placement;

namespace UnitTests.ActivationsLifeCycleTests
{
    /// <summary>
    /// Tests for grain deactivation on idle behavior and related stress scenarios.
    /// </summary>
    [TestCategory("ActivationCollector")]
    [TestArea("Runtime")]
    public class DeactivateOnIdleTests : OrleansTestingBase, IDisposable
    {
        private readonly ITestOutputHelper output;
        private TestCluster testCluster = null!;

        public DeactivateOnIdleTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        private void Initialize(TestClusterBuilder? builder = null)
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
                testCluster = null!;
            }
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [TestArea("Runtime")]
        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdleTestInside_Basic()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            Initialize();

            var a = this.testCluster.GrainFactory!.GetGrain<ICollectionTestGrain>(1);
            var b = this.testCluster.GrainFactory!.GetGrain<ICollectionTestGrain>(2);
            await a.SetOther(b);
            await a.GetOtherAge(); // prime a's routing cache
            await b.DeactivateSelf();
            await Task.Delay(5000, cancellationToken);
            var age = await a.GetOtherAge().WaitAsync(TimeSpan.FromMilliseconds(2000), cancellationToken);
            Assert.True(age.TotalMilliseconds < 2000, "Should be newly activated grain");
        }

        [TestSuite("SlowBVT")]
        [TestProvider("None")]
        [TestArea("Runtime")]
        [Fact, TestCategory("SlowBVT")]
        public async Task DeactivateOnIdleTest_Stress_1()
        {
            Initialize();

            var a = this.testCluster.GrainFactory!.GetGrain<ICollectionTestGrain>(1);
            await a.GetAge();
            await a.DeactivateSelf();
            for (int i = 0; i < 30; i++)
            {
                await a.GetAge();
            }
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [TestArea("Runtime")]
        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdleTest_Stress_2_NonReentrant()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            Initialize();
            var a = this.testCluster.GrainFactory!.GetGrain<ICollectionTestGrain>(1, "UnitTests.Grains.CollectionTestGrain");
            await a.IncrCounter();

            Task t1 = Task.Run(async () =>
            {
                List<Task> tasks = new List<Task>();
                for (int i = 0; i < 100; i++)
                {
                    tasks.Add(a.IncrCounter());
                }
                await Task.WhenAll(tasks).WaitAsync(cancellationToken);
            }, cancellationToken);

            await Task.Delay(1, cancellationToken);
            Task t2 = a.DeactivateSelf();
            await Task.WhenAll(t1, t2).WaitAsync(cancellationToken);
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [TestArea("Runtime")]
        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdleTest_Stress_3_Reentrant()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            Initialize();
            var a = this.testCluster.GrainFactory!.GetGrain<ICollectionTestGrain>(1, "UnitTests.Grains.ReentrantCollectionTestGrain");
            await a.IncrCounter();

            Task t1 = Task.Run(async () =>
            {
                List<Task> tasks = new List<Task>();
                for (int i = 0; i < 100; i++)
                {
                    tasks.Add(a.IncrCounter());
                }
                await Task.WhenAll(tasks).WaitAsync(cancellationToken);
            }, cancellationToken);

            await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
            Task t2 = a.DeactivateSelf();
            await Task.WhenAll(t1, t2).WaitAsync(cancellationToken);
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [TestArea("Runtime")]
        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdleTest_Stress_4_Timer()
        {
            Initialize();
            var a = this.testCluster.GrainFactory!.GetGrain<ICollectionTestGrain>(1, "UnitTests.Grains.ReentrantCollectionTestGrain");
            for (int i = 0; i < 10; i++)
            {
                await a.StartTimer(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(100));
            }
            await a.DeactivateSelf();
            await a.IncrCounter();
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [TestArea("Runtime")]
        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdleTest_Stress_5()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            Initialize();
            var a = this.testCluster.GrainFactory!.GetGrain<ICollectionTestGrain>(1);
            await a.IncrCounter();

            Task t1 = Task.Run(async () =>
            {
                List<Task> tasks = new List<Task>();
                for (int i = 0; i < 100; i++)
                {
                    tasks.Add(a.IncrCounter());
                }
                await Task.WhenAll(tasks).WaitAsync(cancellationToken);
            }, cancellationToken);
            Task t2 = Task.Run(async () =>
            {
                List<Task> tasks = new List<Task>();
                for (int i = 0; i < 1; i++)
                {
                    await Task.Delay(1, cancellationToken);
                    tasks.Add(a.DeactivateSelf());
                }
                await Task.WhenAll(tasks).WaitAsync(cancellationToken);
            }, cancellationToken);
            await Task.WhenAll(t1, t2).WaitAsync(cancellationToken);
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [Fact, TestCategory("Stress")]
        public async Task DeactivateOnIdleTest_Stress_11()
        {
            Initialize();
            var a = this.testCluster.GrainFactory!.GetGrain<ICollectionTestGrain>(1);
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < 100; i++)
            {
                tasks.Add(a.IncrCounter());
            }
            await Task.WhenAll(tasks);
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [TestArea("Runtime")]
        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdle_NonExistentActivation_1()
        {
            await DeactivateOnIdle_NonExistentActivation_Runner(0, TestContext.Current.CancellationToken);
        }

        [TestSuite("Functional")]
        [TestProvider("None")]
        [TestArea("Runtime")]
        [Fact, TestCategory("Functional")]
        public async Task DeactivateOnIdle_NonExistentActivation_2()
        {
            await DeactivateOnIdle_NonExistentActivation_Runner(1, TestContext.Current.CancellationToken);
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
                var maxForwardCount = int.Parse(cfg["MaxForwardCount"]!);
                hostBuilder.ConfigureServices(services =>
                {
                    services.Configure<SiloMessagingOptions>(options => options.MaxForwardCount = maxForwardCount);
                });
            }
        }

        private async Task DeactivateOnIdle_NonExistentActivation_Runner(int forwardCount, CancellationToken cancellationToken)
        {
            var builder = new TestClusterBuilder(2);
            builder.AddClientBuilderConfigurator<ClientConfigurator>();
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            builder.Properties["MaxForwardCount"] = forwardCount.ToString();
            Initialize(builder);

            ICollectionTestGrain grain = await PickGrainInNonPrimary(cancellationToken);

            output.WriteLine("About to make a 1st GetAge() call.");
            TimeSpan age = await grain.GetAge();
            output.WriteLine(age.ToString());

            await grain.DeactivateSelf();
            await Task.Delay(3000, cancellationToken);

            var thrownException = await Record.ExceptionAsync(() => grain.GetAge().WaitAsync(cancellationToken));
            Assert.Null(thrownException);
            output.WriteLine("\nThe 1st call after DeactivateSelf has NOT thrown any exception as expected, since forwardCount is {0}.\n", forwardCount);
        }

        private async Task<ICollectionTestGrain> PickGrainInNonPrimary(CancellationToken cancellationToken)
        {
            var targetSilo = this.testCluster.SecondarySilos.First().SiloAddress;
            var directoryView = await WaitForDirectoryView(targetSilo, cancellationToken);
            var grainType = this.testCluster.GrainFactory!.GetGrain<ICollectionTestGrain>(0).GetGrainId().Type;

            const int maxCandidateGrainKeys = 1_000_000;
            const int candidateYieldInterval = 4096;
            for (int i = 0; i < maxCandidateGrainKeys; i++)
            {
                if (i > 0 && i % candidateYieldInterval == 0)
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // Create grain such that:
                // Its directory owner is not the Gateway silo. This way Gateway will use its directory cache.
                // Its activation is located on the non Gateway silo as well.
                var grainId = GrainId.Create(grainType, GrainIdKeyExtensions.CreateIntegerKey(i));
                if (!directoryView.TryGetOwner(grainId, out var primaryForGrain, out _) || !primaryForGrain.Equals(targetSilo))
                {
                    continue;
                }

                ICollectionTestGrain grain = this.testCluster.GrainFactory!.GetGrain<ICollectionTestGrain>(grainId);
                string siloHostingActivation;
                try
                {
                    RequestContext.Set(IPlacementDirector.PlacementHintKey, targetSilo);
                    siloHostingActivation = await grain.GetRuntimeInstanceId().WaitAsync(cancellationToken);
                }
                finally
                {
                    RequestContext.Remove(IPlacementDirector.PlacementHintKey);
                }

                if (this.testCluster.Primary!.SiloAddress.ToString().Equals(siloHostingActivation, StringComparison.Ordinal))
                {
                    continue;
                }
                this.output.WriteLine("\nCreated grain with key {0} whose primary directory owner is silo {1} and which was activated on silo {2}\n", i, primaryForGrain.ToString(), siloHostingActivation);
                return grain;
            }

            Assert.True(testCluster.GetActiveSilos().Count() > 1, "This logic requires at least 1 non-primary active silo");
            Assert.Fail($"Could not find a grain that activates on a non-primary silo, and has the partition be also managed by a non-primary silo after checking {maxCandidateGrainKeys} integer keys. Target silo {targetSilo} owns {directoryView.GetMemberRanges(targetSilo)} of the directory ring.");
            return null;
        }

        private async Task<DirectoryMembershipSnapshot> WaitForDirectoryView(SiloAddress targetSilo, CancellationToken cancellationToken)
        {
            var directoryMembership = ((InProcessSiloHandle)this.testCluster.Primary!).ServiceProvider.GetRequiredService<DirectoryMembershipService>();
            using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeoutCancellation.Token, cancellationToken);
            try
            {
                await foreach (var view in directoryMembership.ViewUpdates.WithCancellation(cancellation.Token))
                {
                    if (view.Members.Contains(targetSilo))
                    {
                        return view;
                    }
                }
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Assert.Fail($"Timed out waiting for target silo {targetSilo} to join the directory view.");
            }

            Assert.Fail($"Directory view updates completed before target silo {targetSilo} joined the directory view.");
            return null;
        }
    }
}
