using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// Derives service views from cluster membership and fixed assignment configuration.
/// </summary>
internal sealed partial class MembershipBasedClusterServiceViewProvider : IClusterServiceViewProvider
{
    private readonly IClusterMembershipService _clusterMembershipService;
    private readonly ClusterServiceConfiguration _configuration;
    private readonly Func<SiloAddress, int, uint[]> _getRingBoundaries;
    private readonly long _providerEpoch;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly AsyncEnumerable<MembershipBasedClusterServiceView> _viewUpdates;
    private readonly Task _runTask;

    public MembershipBasedClusterServiceViewProvider(
        IClusterMembershipService clusterMembershipService,
        ClusterServiceConfiguration configuration,
        Func<SiloAddress, int, uint[]> getRingBoundaries,
        ILogger logger,
        ClusterMembershipSnapshot? initialSnapshot = null,
        long providerEpoch = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(providerEpoch, 0);
        _clusterMembershipService = clusterMembershipService;
        _configuration = configuration;
        _getRingBoundaries = getRingBoundaries;
        _providerEpoch = providerEpoch;
        _logger = logger;

        CurrentView = new(
            initialSnapshot ?? clusterMembershipService.CurrentSnapshot,
            configuration,
            getRingBoundaries,
            providerEpoch);
        _viewUpdates = new(
            CurrentView,
            static (previous, proposed) =>
                proposed.Id > previous.Id,
            update => CurrentView = update);

        using var _ = new ExecutionContextSuppressor();
        _runTask = Task.Run(ProcessMembershipUpdates);
    }

    public MembershipBasedClusterServiceView CurrentView { get; private set; }

    public IAsyncEnumerable<MembershipBasedClusterServiceView> ViewUpdates => _viewUpdates;

    public IClusterMembershipService ClusterMembershipService => _clusterMembershipService;

    public long ProviderEpoch => _providerEpoch;

    ClusterServiceView IClusterServiceViewProvider.CurrentView => CurrentView;

    IAsyncEnumerable<ClusterServiceView> IClusterServiceViewProvider.ViewUpdates => ViewUpdates;

    async ValueTask<ClusterServiceView> IClusterServiceViewProvider.RefreshViewAsync(
        ClusterServiceViewId? minimumView,
        CancellationToken cancellationToken) => await RefreshViewAsync(minimumView, cancellationToken);

    public async ValueTask<MembershipBasedClusterServiceView> RefreshViewAsync(
        ClusterServiceViewId? minimumView,
        CancellationToken cancellationToken)
    {
        if (minimumView is { } requestedView && requestedView.ProviderEpoch != _providerEpoch)
        {
            throw new InvalidOperationException(
                $"Service '{_configuration.ServiceId}' uses provider epoch {_providerEpoch} and cannot refresh to epoch {requestedView.ProviderEpoch}.");
        }

        if (minimumView is { } minimum && CurrentView.Id >= minimum)
        {
            return CurrentView;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        await _clusterMembershipService.Refresh(
            minimumView is { } requested ? new MembershipVersion(requested.Version.Value) : default,
            linkedCts.Token);
        var requiredView = minimumView ?? new(_providerEpoch, new(_clusterMembershipService.CurrentSnapshot.Version.Value));
        if (CurrentView.Id < requiredView)
        {
            await foreach (var view in _viewUpdates.WithCancellation(linkedCts.Token))
            {
                if (view.Id >= requiredView)
                {
                    return view;
                }
            }

            throw new OperationCanceledException(
                "Cluster service membership updates completed before the requested view was published.",
                linkedCts.Token);
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
                        _viewUpdates.TryPublish(new(update, _configuration, _getRingBoundaries, _providerEpoch));
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
            _shutdownCts.Cancel();
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
