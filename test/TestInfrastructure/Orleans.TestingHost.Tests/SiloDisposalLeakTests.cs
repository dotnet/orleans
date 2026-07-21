using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.Placement;
using Orleans.TestingHost.Tests.Grains;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests
{
    /// <summary>
    /// Regression tests for https://github.com/dotnet/orleans/issues/10258.
    ///
    /// Stopping an in-process silo (e.g. via <see cref="InProcessTestCluster.StopAllSilosAsync"/>)
    /// before disposing the cluster must still dispose the underlying <c>SiloHost</c>. If it does not,
    /// the host's <see cref="IServiceProvider"/> and its background threads (such as the Watchdog) are
    /// never released, leaking the entire silo for the lifetime of the process.
    /// </summary>
    [TestCategory("Functional")]
    public class SiloDisposalLeakTests
    {
        [Fact]
        public async Task StoppedSilo_AfterClusterDispose_HostIsDisposed()
        {
            var builder = new InProcessTestClusterBuilder(1);
            builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
            var cluster = builder.Build();
            await cluster.DeployAsync();

            var serviceProvider = cluster.Silos[0].ServiceProvider;

            // Sanity check: the service provider is usable while the silo is running.
            Assert.NotNull(serviceProvider.GetService<ILocalSiloDetails>());

            // The pattern from the issue: stop the silos first, then dispose the cluster.
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();

            // If the host was disposed, its service provider is disposed too and resolving throws.
            // Prior to the fix the host was never disposed when the silo had already been stopped, so
            // this resolution would succeed and the silo (including its Watchdog threads) would be retained.
            Assert.Throws<ObjectDisposedException>(() => serviceProvider.GetService<ILocalSiloDetails>());
        }

        [Fact]
        public async Task StoppedSilo_AfterHandleDispose_HostIsDisposed()
        {
            var builder = new InProcessTestClusterBuilder(1);
            builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
            var cluster = builder.Build();
            await cluster.DeployAsync();

            var handle = cluster.Silos[0];
            var serviceProvider = handle.ServiceProvider;

            // Stop the silo, then dispose the handle directly. Disposal must dispose the host even
            // though the handle is no longer active.
            await handle.StopSiloAsync(stopGracefully: true);
            await handle.DisposeAsync();

            Assert.Throws<ObjectDisposedException>(() => serviceProvider.GetService<ILocalSiloDetails>());

            await cluster.DisposeAsync();
        }

        [Fact]
        public void StoppedSilo_AfterClusterDispose_ServiceProviderIsCollected()
        {
            // Deploy, stop, and dispose entirely within a non-inlined synchronous helper that returns
            // only a WeakReference. This keeps the cluster and every intermediate local out of this
            // frame so that, once the helper returns, nothing roots the silo's service provider except
            // (before the fix) the still-running host and its background threads.
            var weakRef = DeployStopAndDispose();

            AssertCollected(weakRef);
        }

        [Fact]
        public void KilledSilo_AfterClusterDispose_ServiceProviderIsCollected()
        {
            var weakRef = DeployKillAndDispose();

            AssertCollected(weakRef);
        }

        [Fact]
        public async Task KilledSilo_WithInFlightOutboundCall_DisposesPromptly()
        {
            const int PendingCallCount = 8;

            // Regression test for the deadlock that surfaced in CI: killing a silo whose stateless
            // workers are awaiting outbound calls (whose responses can never arrive once the silo stops)
            // must not hang while disposing the silo host. A stateless worker's grain context disposal
            // awaits each worker's deactivation. Faulting those calls resumes the workers, which retry
            // and would register new callbacks after the shutdown sweep unless registration is closed.
            var builder = new InProcessTestClusterBuilder(2);
            builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
            await using var cluster = builder.Build();
            await cluster.DeployAsync();

            var client = cluster.Client;
            var launcherHandle = cluster.Silos[0];
            var remoteHandle = cluster.Silos[1];

            // Place the launcher on the silo which will be killed. The stateless workers it invokes are
            // placed on that same silo.
            RequestContext.Set(IPlacementDirector.PlacementHintKey, launcherHandle.SiloAddress);
            var launcher = client.GetGrain<ILauncherGrain>(Guid.NewGuid());
            try
            {
                Assert.Equal(launcherHandle.SiloAddress.ToString(), await launcher.GetSiloIdentity());
            }
            finally
            {
                RequestContext.Remove(IPlacementDirector.PlacementHintKey);
            }

            var remoteKeys = new List<Guid>(PendingCallCount);
            for (var call = 0; call < PendingCallCount; call++)
            {
                // Place each blocker on the surviving silo so the worker's call is a genuine outbound
                // request whose response is severed when the launcher's silo is killed.
                var remoteKey = Guid.NewGuid();
                RequestContext.Set(IPlacementDirector.PlacementHintKey, remoteHandle.SiloAddress);
                var remote = client.GetGrain<IRemoteBlockerGrain>(remoteKey);
                try
                {
                    Assert.Equal(remoteHandle.SiloAddress.ToString(), await remote.GetSiloIdentity());
                }
                finally
                {
                    RequestContext.Remove(IPlacementDirector.PlacementHintKey);
                }

                remoteKeys.Add(remoteKey);

                // Trigger the outbound call from a stateless worker co-located on the launcher's silo.
                var worker = client.GetGrain<ILocalWorkerGrain>(Guid.NewGuid());
                await launcher.StartBlockingCall(worker, remote);
                Assert.True(await RemoteBlockerGrain.WaitForEntered(remoteKey, TimeSpan.FromSeconds(30)), "The blocking call was never entered.");
            }

            // Kill the launcher's silo (ungraceful) and dispose its host. Prior to the fix this can hang
            // if cancellation interrupts callback timer shutdown before the callbacks are faulted.
            var killAndDispose = cluster.KillSiloAsync(launcherHandle);
            var completed = await Task.WhenAny(killAndDispose, Task.Delay(TimeSpan.FromSeconds(60)));
            Assert.True(completed == killAndDispose, "Killing a silo with an in-flight outbound call should not hang host disposal.");
            await killAndDispose;

            // Allow the surviving silo to shut down cleanly.
            foreach (var remoteKey in remoteKeys)
            {
                RemoteBlockerGrain.Release(remoteKey);
            }
        }

        private static void AssertCollected(WeakReference weakRef)
        {
            // Background threads (e.g. the Watchdog) can briefly keep the provider rooted on their stack
            // while they wind down, so retry with a bounded timeout rather than asserting after a single GC.
            for (var attempt = 0; attempt < 20 && weakRef.IsAlive; attempt++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

                if (weakRef.IsAlive)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }

            Assert.False(weakRef.IsAlive, "The stopped silo's service provider should be collectible after the cluster is disposed.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference DeployStopAndDispose()
        {
            var cluster = BuildCluster();
            cluster.DeployAsync().GetAwaiter().GetResult();
            var weakRef = new WeakReference(cluster.Silos[0].ServiceProvider);
            cluster.StopAllSilosAsync().GetAwaiter().GetResult();
            cluster.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return weakRef;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference DeployKillAndDispose()
        {
            var cluster = BuildCluster();
            cluster.DeployAsync().GetAwaiter().GetResult();
            var weakRef = new WeakReference(cluster.Silos[0].ServiceProvider);
            cluster.KillSiloAsync(cluster.Silos[0]).GetAwaiter().GetResult();
            cluster.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return weakRef;
        }

        private static InProcessTestCluster BuildCluster()
        {
            var builder = new InProcessTestClusterBuilder(1);
            builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
            return builder.Build();
        }
    }
}
