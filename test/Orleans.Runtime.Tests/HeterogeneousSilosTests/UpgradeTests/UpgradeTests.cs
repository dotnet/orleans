using Microsoft.Extensions.DependencyInjection;
using Orleans.Metadata;
using Orleans.Versions.Compatibility;
using Orleans.Versions.Selector;
using TestVersionGrainInterfaces;
using Xunit;

namespace Tester.HeterogeneousSilosTests.UpgradeTests
{
    /// <summary>
    /// Tests for minimum version selector strategy ensuring v1 grains are always activated.
    /// </summary>
    [TestSuite("SlowBVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT")]
    public class MinimumVersionTests : UpgradeTestsBase
    {
        protected override Type VersionSelectorStrategy => typeof(MinimumVersion);
        protected override Type CompatibilityStrategy => typeof(BackwardCompatible);

        [Fact]
        public Task AlwaysCreateActivationWithMinimumVersion()
        {
            // Even after v2 silo is deployed, we should only activate v1 grains
            return Step1_StartV1Silo_Step2_StartV2Silo_Step3_StopV2Silo(step2Version: 1);
        }
    }

    /// <summary>
    /// Tests for latest version selector strategy with backward compatibility and grain upgrades.
    /// </summary>
    [TestSuite("SlowBVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT")]
    public class LatestVersionTests : UpgradeTestsBase
    {
        protected override Type VersionSelectorStrategy => typeof(LatestVersion);
        protected override Type CompatibilityStrategy => typeof(BackwardCompatible);
        protected override bool WaitForCancellationAcknowledgement => true;

        [Fact]
        public Task AlwaysCreateActivationWithLatestVersion()
        {
            // After v2 is deployed, we should always activate v2 grains
            return Step1_StartV1Silo_Step2_StartV2Silo_Step3_StopV2Silo(step2Version: 2);
        }

        [Fact]
        public Task UpgradeProxyCallNoPendingRequest()
        {
            // v2 -> v1 call should provoke grain activation upgrade.
            // The grain is inactive when receiving the message
            return ProxyCallNoPendingRequest(expectedVersion: 2);
        }

        [Fact]
        public Task UpgradeProxyCallWithPendingRequest()
        {
            // v2 -> v1 call should provoke grain activation upgrade
            // The grain is already processing a request when receiving the message
            return ProxyCallWithPendingRequest(expectedVersion: 2);
        }
    }

    /// <summary>
    /// Tests for all versions compatible strategy preventing automatic grain upgrades.
    /// </summary>
    [TestSuite("SlowBVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT")]
    public class AllVersionsCompatibleTests : UpgradeTestsBase
    {
        protected override Type VersionSelectorStrategy => typeof(LatestVersion);
        protected override Type CompatibilityStrategy => typeof(AllVersionsCompatible);

        [Fact]
        public Task DoNotUpgradeProxyCallNoPendingRequest()
        {
            // v2 -> v1 call should provoke grain activation upgrade because they are compatible
            // The grain is inactive when receiving the message
            return ProxyCallNoPendingRequest(expectedVersion: 1);
        }

        [Fact]
        public Task DoNotUpgradeProxyCallWithPendingRequest()
        {
            // v2 -> v1 call should provoke grain activation upgrade because they are compatible
            // The grain is already processing a request when receiving the message
            return ProxyCallWithPendingRequest(expectedVersion: 1);
        }
    }

    /// <summary>
    /// Tests requests for methods introduced during rolling upgrades.
    /// </summary>
    [TestSuite("SlowBVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestArea("Versioning")]
    public class UndecodableRequestUpgradeTests : UpgradeTestsBase
    {
        protected override Type VersionSelectorStrategy => typeof(LatestVersion);
        protected override Type CompatibilityStrategy => typeof(BackwardCompatible);

        [Fact]
        public async Task IncompatibleOldSiloForwardsVersion2Request()
        {
            await StartSiloV1();

            var target = Client.GetGrain<IVersionUpgradeTestGrain>(0);
            Assert.Equal(1, await target.GetVersion());

            await StartSiloV2();

            var caller = Client.GetGrain<IVersionUpgradeTestGrain>(1);
            Assert.Equal(2, await caller.GetVersion());

            var resolver = Client.ServiceProvider.GetRequiredService<GrainInterfaceTypeResolver>();
            var interfaceType = resolver.GetGrainInterfaceType(typeof(IVersionUpgradeTestGrain));

            var barrier = new UpgradeBarrier();
            var observer = Client.CreateObjectReference<IVersionUpgradeTestObserver>(barrier);
            try
            {
                var call = caller.ProxyCallVersion2MethodAfterBarrier(target, observer);
                await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

                await ManagementGrain.SetCompatibilityStrategy(interfaceType, StrictVersionCompatible.Singleton);
                barrier.Release();

                Assert.Equal(2, await call);
            }
            finally
            {
                barrier.Release();
                Client.DeleteObjectReference<IVersionUpgradeTestObserver>(observer);
            }
        }

        [Fact]
        public async Task CompatibleOldSiloRejectsUndecodableVersion2RequestAtDispatch()
        {
            await StartSiloV1();

            var target = Client.GetGrain<IVersionUpgradeTestGrain>(0);
            Assert.Equal(1, await target.GetVersion());

            await StartSiloV2();

            var caller = Client.GetGrain<IVersionUpgradeTestGrain>(1);
            Assert.Equal(2, await caller.GetVersion());

            var resolver = Client.ServiceProvider.GetRequiredService<GrainInterfaceTypeResolver>();
            var interfaceType = resolver.GetGrainInterfaceType(typeof(IVersionUpgradeTestGrain));
            await ManagementGrain.SetCompatibilityStrategy(interfaceType, AllVersionsCompatible.Singleton);

            var exception = await Assert.ThrowsAsync<NotSupportedException>(() => caller.ProxyCallVersion2Method(target));
            Assert.Contains("undecoded request with unavailable invokable alias", exception.Message);
        }

        [Fact]
        public async Task QueuedUndecodableRequestAcknowledgesCancellation()
        {
            await StartSiloV1();

            var target = Client.GetGrain<IVersionUpgradeTestGrain>(0);
            Assert.Equal(1, await target.GetVersion());

            await StartSiloV2();

            var caller = Client.GetGrain<IVersionUpgradeTestGrain>(1);
            Assert.Equal(2, await caller.GetVersion());

            var resolver = Client.ServiceProvider.GetRequiredService<GrainInterfaceTypeResolver>();
            var interfaceType = resolver.GetGrainInterfaceType(typeof(IVersionUpgradeTestGrain));
            await ManagementGrain.SetCompatibilityStrategy(interfaceType, AllVersionsCompatible.Singleton);

            var targetBarrier = new UpgradeBarrier();
            var targetObserver = Client.CreateObjectReference<IVersionUpgradeTestObserver>(targetBarrier);
            var callerBarrier = new UpgradeBarrier();
            var callerObserver = Client.CreateObjectReference<IVersionUpgradeTestObserver>(callerBarrier);
            using var cancellation = new CancellationTokenSource();
            try
            {
                var blockingCall = target.WaitForRelease(targetObserver);
                await targetBarrier.Entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

                var call = caller.ProxyCallCancellableVersion2MethodAfterBarrier(target, callerObserver, cancellation.Token);
                await callerBarrier.Entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

                cancellation.Cancel();
                callerBarrier.Release();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => call.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

                targetBarrier.Release();
                await blockingCall.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
            finally
            {
                callerBarrier.Release();
                targetBarrier.Release();
                Client.DeleteObjectReference<IVersionUpgradeTestObserver>(callerObserver);
                Client.DeleteObjectReference<IVersionUpgradeTestObserver>(targetObserver);
            }
        }

        [Fact]
        public async Task IncompatibleOldSiloForwardsVersion2OneWayRequest()
        {
            await StartSiloV1();

            var target = Client.GetGrain<IVersionUpgradeTestGrain>(0);
            Assert.Equal(1, await target.GetVersion());

            await StartSiloV2();

            var caller = Client.GetGrain<IVersionUpgradeTestGrain>(1);
            Assert.Equal(2, await caller.GetVersion());

            var resolver = Client.ServiceProvider.GetRequiredService<GrainInterfaceTypeResolver>();
            var interfaceType = resolver.GetGrainInterfaceType(typeof(IVersionUpgradeTestGrain));

            var callerBarrier = new UpgradeBarrier();
            var callerObserver = Client.CreateObjectReference<IVersionUpgradeTestObserver>(callerBarrier);
            var deliveryBarrier = new UpgradeBarrier();
            var deliveryObserver = Client.CreateObjectReference<IVersionUpgradeTestObserver>(deliveryBarrier);
            try
            {
                var call = caller.ProxyCallVersion2OneWayMethodAfterBarrier(target, callerObserver, deliveryObserver);
                await callerBarrier.Entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

                await ManagementGrain.SetCompatibilityStrategy(interfaceType, StrictVersionCompatible.Singleton);
                callerBarrier.Release();

                await call.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                await deliveryBarrier.Entered.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
            finally
            {
                callerBarrier.Release();
                deliveryBarrier.Release();
                Client.DeleteObjectReference<IVersionUpgradeTestObserver>(deliveryObserver);
                Client.DeleteObjectReference<IVersionUpgradeTestObserver>(callerObserver);
            }
        }

        private sealed class UpgradeBarrier : IVersionUpgradeTestObserver
        {
            private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Entered => _entered.Task;

            public Task WaitForRelease()
            {
                _entered.TrySetResult();
                return _release.Task;
            }

            public void Release() => _release.TrySetResult();
        }
    }

    /// <summary>
    /// Tests for random compatible version selection strategy distributing activations across versions.
    /// </summary>
    [TestSuite("SlowBVT")]
    [TestProvider("None")]
    [TestArea("Runtime")]
    [TestCategory("Versioning"), TestCategory("ExcludeXAML"), TestCategory("SlowBVT")]
    public class RandomCompatibleVersionTests : UpgradeTestsBase
    {
        protected override Type VersionSelectorStrategy => typeof(AllCompatibleVersions);
        protected override Type CompatibilityStrategy => typeof(AllVersionsCompatible);

        [Fact]
        public async Task CreateActivationWithBothVersion()
        {
            const float numberOfGrains = 300;

            await StartSiloV1();
            await StartSiloV2();

            var versionCounter = new int[2];

            // We should create v1 and v2 activations

            for (var i = 0; i < numberOfGrains; i++)
            {
                var v = await Client.GetGrain<IVersionUpgradeTestGrain>(i).GetVersion();
                versionCounter[v - 1]++;
            }

            // 99.95% chance of success
            Assert.InRange(versionCounter[0] / numberOfGrains, 0.35, 0.65);
            Assert.InRange(versionCounter[1] / numberOfGrains, 0.35, 0.65);
        }
    }
}
