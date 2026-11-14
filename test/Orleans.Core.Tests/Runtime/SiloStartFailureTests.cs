using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

/// <summary>
/// A silo whose start fails or is interrupted stops the lifecycle stages which had already started (#11674).
/// </summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
[TestCategory("BVT"), TestCategory("Lifecycle")]
public class SiloStartFailureTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task FailedStart_StopsTheStartedStagesBeforeRethrowing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var participant = new StartParticipant(blockUntilCancelled: false);
        var (siloPort, gatewayPort) = GetFreePorts();
        using var host = BuildHost(participant, siloPort, gatewayPort);
        var silo = host.Services.GetRequiredService<Silo>();
        var membership = host.Services.GetRequiredService<IMembershipManager>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(cancellationToken)).WaitAsync(Timeout, cancellationToken);

        Assert.Same(participant.Failure, exception);
        Assert.True(participant.ReachedAfterJoining);
        Assert.True(silo.SiloTerminated.IsCompletedSuccessfully);
        Assert.Equal(SystemStatus.Terminated, silo.SystemStatus);
        Assert.Equal(SiloStatus.Dead, membership.LocalSiloStatus);
        Assert.False(await CanConnectAsync(siloPort, cancellationToken));

        // Stopping the host afterwards completes instead of throwing.
        await host.StopAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringStart_CancelsTheStartAndStopsTheStartedStages(bool dispose)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var participant = new StartParticipant(blockUntilCancelled: true);
        var (siloPort, gatewayPort) = GetFreePorts();
        using var host = BuildHost(participant, siloPort, gatewayPort);
        var silo = host.Services.GetRequiredService<Silo>();
        var membership = host.Services.GetRequiredService<IMembershipManager>();

        var start = host.StartAsync(cancellationToken);
        await participant.Blocked.WaitAsync(Timeout, cancellationToken);
        Assert.Equal(SiloStatus.Joining, membership.LocalSiloStatus);
        Assert.True(await CanConnectAsync(siloPort, cancellationToken));

        if (dispose)
        {
            // Non-graceful: DisposeAsync passes a cancelled token to StopAsync.
            await silo.DisposeAsync().AsTask().WaitAsync(Timeout, cancellationToken);
        }
        else
        {
            await host.StopAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start).WaitAsync(Timeout, cancellationToken);
        Assert.True(silo.SiloTerminated.IsCompletedSuccessfully);
        Assert.Equal(SystemStatus.Terminated, silo.SystemStatus);
        Assert.Equal(SiloStatus.Dead, membership.LocalSiloStatus);
        Assert.False(await CanConnectAsync(siloPort, cancellationToken));
    }

    [Fact]
    public async Task StopBeforeStart_TerminatesTheSilo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (siloPort, gatewayPort) = GetFreePorts();
        using var host = BuildHost(new StartParticipant(blockUntilCancelled: false), siloPort, gatewayPort);
        var silo = host.Services.GetRequiredService<Silo>();

        await silo.StopAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);

        Assert.True(silo.SiloTerminated.IsCompletedSuccessfully);
        Assert.Equal(SystemStatus.Terminated, silo.SystemStatus);
        await Assert.ThrowsAsync<InvalidOperationException>(() => silo.StartAsync(cancellationToken));
    }

    private static IHost BuildHost(StartParticipant participant, int siloPort, int gatewayPort) =>
        new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .UseOrleans(silo => silo
                .UseLocalhostClustering(siloPort, gatewayPort)
                .ConfigureServices(services => services.AddSingleton<ILifecycleParticipant<ISiloLifecycle>>(participant)))
            .Build();

    private static (int SiloPort, int GatewayPort) GetFreePorts()
    {
        using var silo = new TcpListener(IPAddress.Loopback, 0);
        using var gateway = new TcpListener(IPAddress.Loopback, 0);
        silo.Start();
        gateway.Start();
        return (((IPEndPoint)silo.LocalEndpoint).Port, ((IPEndPoint)gateway.LocalEndpoint).Port);
    }

    private static async Task<bool> CanConnectAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs at <see cref="ServiceLifecycleStage.ApplicationServices"/>, after the silo listener has started and the
    /// silo has written its <see cref="SiloStatus.Joining"/> row, and either fails or blocks until the start is cancelled.
    /// </summary>
    private sealed class StartParticipant(bool blockUntilCancelled) : ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InvalidOperationException Failure { get; } = new("Start failed after the silo started joining.");

        public bool ReachedAfterJoining { get; private set; }

        public Task Blocked => _blocked.Task;

        public void Participate(ISiloLifecycle lifecycle) =>
            lifecycle.Subscribe(nameof(StartParticipant), ServiceLifecycleStage.ApplicationServices, OnStart, _ => Task.CompletedTask);

        private async Task OnStart(CancellationToken cancellationToken)
        {
            ReachedAfterJoining = true;
            if (!blockUntilCancelled)
            {
                throw Failure;
            }

            _blocked.SetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
