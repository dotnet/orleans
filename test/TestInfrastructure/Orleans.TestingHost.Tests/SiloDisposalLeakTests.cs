using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
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
            // Regression test for the deadlock that surfaced in CI: killing a silo whose stateless
            // worker is awaiting an outbound call (whose response can never arrive once the silo stops)
            // must not hang while disposing the silo host. A stateless worker's grain context disposal
            // awaits the worker's deactivation, which cannot complete until the outbound call does; the
            // runtime faults outstanding callbacks during shutdown so that deactivation can complete.
            var builder = new InProcessTestClusterBuilder(2);
            builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
            await using var cluster = builder.Build();
            await cluster.DeployAsync();

            var client = cluster.Client;

            // The launcher is an identity-placed grain: it gives us a deterministic silo to kill, and
            // the stateless worker it invokes is placed on that same silo.
            var launcher = client.GetGrain<ILauncherGrain>(Guid.NewGuid());
            var launcherSilo = await launcher.GetSiloIdentity();

            // The remote blocker must live on a different silo so the worker's call is a genuine
            // outbound request whose response is severed when the launcher's silo is killed.
            IRemoteBlockerGrain remote = null;
            var remoteKey = Guid.Empty;
            for (var i = 0; i < 200 && remote is null; i++)
            {
                var key = Guid.NewGuid();
                var candidate = client.GetGrain<IRemoteBlockerGrain>(key);
                var silo = await candidate.GetSiloIdentity();
                if (silo != launcherSilo)
                {
                    remote = candidate;
                    remoteKey = key;
                }
            }

            Assert.NotNull(remote);

            var worker = client.GetGrain<ILocalWorkerGrain>(Guid.NewGuid());

            // Trigger the outbound call from a stateless worker co-located on the launcher's silo.
            await launcher.StartBlockingCall(worker, remote);
            Assert.True(await RemoteBlockerGrain.WaitForEntered(remoteKey, TimeSpan.FromSeconds(30)), "The blocking call was never entered.");

            var launcherHandle = cluster.Silos.Single(s => s.SiloAddress.ToString() == launcherSilo);

            // Kill the launcher's silo (ungraceful) and dispose its host. Prior to the fix this hangs.
            var killAndDispose = cluster.KillSiloAsync(launcherHandle);
            var completed = await Task.WhenAny(killAndDispose, Task.Delay(TimeSpan.FromSeconds(60)));
            Assert.True(completed == killAndDispose, "Killing a silo with an in-flight outbound call should not hang host disposal.");
            await killAndDispose;

            // Allow the surviving silo to shut down cleanly.
            RemoteBlockerGrain.Release(remoteKey);
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
