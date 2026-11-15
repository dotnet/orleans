using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Orleans.Services;
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
    public async Task FailedStart_RollbackBlockedInAStopObserver_IsHurriedByALaterDispose()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var participant = new StartParticipant(blockUntilCancelled: false, blockStopUntilCancelled: true);
        var (siloPort, gatewayPort) = GetFreePorts();
        using var host = BuildHost(participant, siloPort, gatewayPort);
        var silo = host.Services.GetRequiredService<Silo>();
        var membership = host.Services.GetRequiredService<IMembershipManager>();

        var start = host.StartAsync(cancellationToken);
        await participant.StopBlocked.WaitAsync(Timeout, cancellationToken);
        Assert.False(start.IsCompleted);

        // DisposeAsync passes a cancelled token, which cancels the rollback's stop token.
        await silo.DisposeAsync().AsTask().WaitAsync(Timeout, cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => start).WaitAsync(Timeout, cancellationToken);
        Assert.Same(participant.Failure, exception);
        Assert.True(silo.SiloTerminated.IsCompletedSuccessfully);
        Assert.Equal(SystemStatus.Terminated, silo.SystemStatus);
        Assert.Equal(SiloStatus.Dead, membership.LocalSiloStatus);
        Assert.False(await CanConnectAsync(siloPort, cancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeDuringFailedStartRollback_WaitsForTheRollbackToComplete(bool disposeAsync)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var cleanupBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var participant = new StartParticipant(blockUntilCancelled: false, blockStopUntilCancelled: true, stopCleanup: cleanupBarrier.Task);
        var (siloPort, gatewayPort) = GetFreePorts();
        using var host = BuildHost(participant, siloPort, gatewayPort);
        var silo = host.Services.GetRequiredService<Silo>();
        var membership = host.Services.GetRequiredService<IMembershipManager>();

        var start = host.StartAsync(cancellationToken);
        Task dispose;
        try
        {
            await participant.StopBlocked.WaitAsync(Timeout, cancellationToken);

            // Disposal hurries the rollback, whose stop observer then runs its cleanup until the barrier is released.
            dispose = disposeAsync ? silo.DisposeAsync().AsTask() : Task.Run(silo.Dispose, CancellationToken.None);
            await participant.StopCancelled.WaitAsync(Timeout, cancellationToken);

            // The rollback has not reached the membership and listener stages yet, so disposal must still be pending.
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            Assert.False(dispose.IsCompleted);
            Assert.False(silo.SiloTerminated.IsCompleted);
            Assert.True(await CanConnectAsync(siloPort, cancellationToken));
        }
        finally
        {
            // Also released when a wait or assertion above fails, so that disposing the host does not hang the test.
            cleanupBarrier.TrySetResult();
        }

        await dispose.WaitAsync(Timeout, cancellationToken);

        Assert.True(silo.SiloTerminated.IsCompletedSuccessfully);
        Assert.Equal(SystemStatus.Terminated, silo.SystemStatus);
        Assert.Equal(SiloStatus.Dead, membership.LocalSiloStatus);
        Assert.False(await CanConnectAsync(siloPort, cancellationToken));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => start).WaitAsync(Timeout, cancellationToken);
        Assert.Same(participant.Failure, exception);
    }

    [Fact]
    public async Task StopDuringGrainServiceInit_CancelsTheWaitAndStopsTheService()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (siloPort, gatewayPort) = GetFreePorts();
        var initProbe = new GrainServiceInitProbe();
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .UseOrleans(silo => silo
                .UseLocalhostClustering(siloPort, gatewayPort)
                .AddGrainService<BlockedInitGrainService>()
                .ConfigureServices(services => services.AddSingleton(initProbe)))
            .Build();
        var silo = host.Services.GetRequiredService<Silo>();

        // Without cancellation, the start would wait for Init until MaxJoinAttemptTime (5 minutes by default).
        var start = host.StartAsync(cancellationToken);
        await initProbe.Blocked.Task.WaitAsync(Timeout, cancellationToken);

        await host.StopAsync(cancellationToken).WaitAsync(Timeout, cancellationToken);

        // The stop reached the service, whose Init ended before the silo terminated.
        Assert.True(initProbe.Ended.Task.IsCompletedSuccessfully);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start).WaitAsync(Timeout, cancellationToken);
        Assert.True(silo.SiloTerminated.IsCompletedSuccessfully);
        Assert.Equal(SystemStatus.Terminated, silo.SystemStatus);
        Assert.False(await CanConnectAsync(siloPort, cancellationToken));
    }

    [Fact]
    public async Task StopDuringStart_WithAThrowingCancellationCallback_StillStopsTheStartedStages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var participant = new StartParticipant(blockUntilCancelled: true, throwOnStartCancellation: true);
        var (siloPort, gatewayPort) = GetFreePorts();
        using var host = BuildHost(participant, siloPort, gatewayPort);
        var silo = host.Services.GetRequiredService<Silo>();
        var membership = host.Services.GetRequiredService<IMembershipManager>();

        var start = host.StartAsync(cancellationToken);
        await participant.Blocked.WaitAsync(Timeout, cancellationToken);

        // The callback's exception reaches the caller which cancelled the start, but only after the silo has stopped.
        var exception = await Assert.ThrowsAsync<AggregateException>(() => silo.StopAsync(cancellationToken)).WaitAsync(Timeout, cancellationToken);
        Assert.Contains(participant.CancellationCallbackFailure, exception.Flatten().InnerExceptions);

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

    public interface IBlockedInitGrainService : IGrainService
    {
    }

    public sealed class GrainServiceInitProbe
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A grain service whose <see cref="GrainService.Init"/> runs until the service is stopped.</summary>
    public sealed class BlockedInitGrainService(GrainId id, Silo silo, ILoggerFactory loggerFactory, GrainServiceInitProbe probe)
        : GrainService(id, silo, loggerFactory), IBlockedInitGrainService
    {
        public override async Task Init(IServiceProvider serviceProvider)
        {
            probe.Blocked.TrySetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, StoppedCancellationTokenSource.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            probe.Ended.TrySetResult();
        }
    }

    /// <summary>
    /// Runs at <see cref="ServiceLifecycleStage.ApplicationServices"/>, after the silo listener has started and the
    /// silo has written its <see cref="SiloStatus.Joining"/> row, and either fails or blocks until the start is cancelled.
    /// Its stop can block until the stop is cancelled, and then wait for <c>stopCleanup</c> before it completes.
    /// With <c>throwOnStartCancellation</c>, it registers a start-token callback which throws.
    /// </summary>
    private sealed class StartParticipant(
        bool blockUntilCancelled,
        bool blockStopUntilCancelled = false,
        Task? stopCleanup = null,
        bool throwOnStartCancellation = false) : ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InvalidOperationException Failure { get; } = new("Start failed after the silo started joining.");

        public InvalidOperationException CancellationCallbackFailure { get; } = new("Start cancellation callback failed.");

        public bool ReachedAfterJoining { get; private set; }

        public Task Blocked => _blocked.Task;

        public Task StopBlocked => _stopBlocked.Task;

        public Task StopCancelled => _stopCancelled.Task;

        public void Participate(ISiloLifecycle lifecycle) =>
            lifecycle.Subscribe(nameof(StartParticipant), ServiceLifecycleStage.ApplicationServices, OnStart, OnStop);

        private async Task OnStop(CancellationToken cancellationToken)
        {
            if (blockStopUntilCancelled)
            {
                _stopBlocked.SetResult();
                try
                {
                    await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (stopCleanup is not null)
                {
                    // Cleanup which still has to finish after the stop is hurried.
                    _stopCancelled.SetResult();
                    await stopCleanup;
                }
            }
        }

        private async Task OnStart(CancellationToken cancellationToken)
        {
            ReachedAfterJoining = true;
            if (!blockUntilCancelled)
            {
                throw Failure;
            }

            using var registration = throwOnStartCancellation
                ? cancellationToken.Register(() => throw CancellationCallbackFailure)
                : default;
            _blocked.SetResult();
            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
