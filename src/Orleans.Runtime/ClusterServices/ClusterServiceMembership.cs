using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime.ClusterServices;

internal sealed partial class ClusterServiceMembership : IAsyncDisposable
{
    private readonly IClusterMembershipService _clusterMembershipService;
    private readonly ClusterServiceConfiguration _configuration;
    private readonly Func<SiloAddress, int, uint[]> _getRingBoundaries;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly AsyncEnumerable<ClusterServiceTopology> _viewUpdates;
    private readonly Task _runTask;

    public ClusterServiceMembership(
        IClusterMembershipService clusterMembershipService,
        ClusterServiceConfiguration configuration,
        Func<SiloAddress, int, uint[]> getRingBoundaries,
        ILogger logger,
        ClusterMembershipSnapshot? initialSnapshot = null)
    {
        _clusterMembershipService = clusterMembershipService;
        _configuration = configuration;
        _getRingBoundaries = getRingBoundaries;
        _logger = logger;

        CurrentView = new(
            initialSnapshot ?? clusterMembershipService.CurrentSnapshot,
            configuration,
            getRingBoundaries);
        _viewUpdates = new(
            CurrentView,
            static (previous, proposed) =>
                proposed.ViewId.MembershipVersion > previous.ViewId.MembershipVersion,
            update => CurrentView = update);

        using var _ = new ExecutionContextSuppressor();
        _runTask = Task.Run(ProcessMembershipUpdates);
    }

    public ClusterServiceTopology CurrentView { get; private set; }

    public IAsyncEnumerable<ClusterServiceTopology> ViewUpdates => _viewUpdates;

    public IClusterMembershipService ClusterMembershipService => _clusterMembershipService;

    public async ValueTask<ClusterServiceTopology> RefreshViewAsync(
        MembershipVersion minimumVersion,
        CancellationToken cancellationToken)
    {
        _clusterMembershipService.Refresh(minimumVersion, cancellationToken).Ignore();
        if (CurrentView.ViewId.MembershipVersion < minimumVersion)
        {
            await foreach (var view in _viewUpdates.WithCancellation(cancellationToken))
            {
                if (view.ViewId.MembershipVersion >= minimumVersion)
                {
                    break;
                }
            }
        }

        return CurrentView;
    }

    private async Task ProcessMembershipUpdates()
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                try
                {
                    await foreach (var update in _clusterMembershipService.MembershipUpdates.WithCancellation(_shutdownCts.Token))
                    {
                        _viewUpdates.TryPublish(new(update, _configuration, _getRingBoundaries));
                    }

                    break;
                }
                catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    LogErrorProcessingMembershipUpdates(_configuration.ServiceId, exception);
                    await Task.Delay(TimeSpan.FromSeconds(1), _shutdownCts.Token);
                }
            }
        }
        finally
        {
            _viewUpdates.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        await _runTask.SuppressThrowing();
        _shutdownCts.Dispose();
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error projecting cluster membership for service '{ServiceId}'."
    )]
    private partial void LogErrorProcessingMembershipUpdates(string serviceId, Exception exception);
}
