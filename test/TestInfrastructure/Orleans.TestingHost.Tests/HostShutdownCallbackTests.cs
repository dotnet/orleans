using System;
using System.Threading.Tasks;
using Orleans.TestingHost.Tests.Grains;
using TestExtensions;
using Xunit;

namespace Orleans.TestingHost.Tests
{
    /// <summary>
    /// Tests that an Orleans host (silo or client) faults its outstanding grain-call callbacks when it
    /// shuts down, so in-flight calls observe a terminal result instead of hanging forever once the
    /// host can no longer receive responses.
    /// </summary>
    [TestCategory("Functional")]
    public class HostShutdownCallbackTests
    {
        [Fact]
        public async Task StoppedClient_WithInFlightCall_FaultsInsteadOfHanging()
        {
            // A client that is stopped while it has an in-flight grain call must fault the outstanding
            // request rather than leave the caller hanging forever. During client shutdown the callback
            // expiry timer is disposed, so unless outstanding callbacks are explicitly faulted the
            // awaiting task never completes.
            var builder = new InProcessTestClusterBuilder(1);
            builder.ConfigureHost(hostBuilder => TestDefaultConfiguration.ConfigureHostConfiguration(hostBuilder.Configuration));
            await using var cluster = builder.Build();
            await cluster.DeployAsync();

            var key = Guid.NewGuid();
            var blocker = cluster.Client.GetGrain<IRemoteBlockerGrain>(key);

            // Start (but do not await) a call to a grain that blocks and never responds.
            var pending = blocker.BlockUntilReleased();
            var followUp = RetryAfterFailure(pending, blocker);
            Assert.True(await RemoteBlockerGrain.WaitForEntered(key, TimeSpan.FromSeconds(30)), "The blocking call was never entered.");

            // Stop the client while the call is in-flight.
            await cluster.StopClusterClientAsync();

            // The pending call must fault promptly instead of hanging.
            var faulted = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(faulted == pending, "Stopping the client should fault in-flight calls instead of hanging.");
            await Assert.ThrowsAnyAsync<Exception>(async () => await pending);

            var followUpFaulted = await Task.WhenAny(followUp, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(followUpFaulted == followUp, "Calls issued after client shutdown begins should fault instead of hanging.");
            await Assert.ThrowsAnyAsync<Exception>(async () => await followUp);

            RemoteBlockerGrain.Release(key);
        }

        private static async Task RetryAfterFailure(Task pending, IRemoteBlockerGrain blocker)
        {
            try
            {
                await pending;
            }
            catch
            {
                await blocker.BlockUntilReleased();
            }
        }
    }
}
