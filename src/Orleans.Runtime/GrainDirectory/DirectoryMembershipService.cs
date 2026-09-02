using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime.ClusterServices;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class DirectoryMembershipService : IAsyncDisposable
{
    private readonly IInternalGrainFactory _grainFactory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _runTask;
    private readonly AsyncEnumerable<DirectoryMembershipSnapshot> _viewUpdates;
    private readonly ClusterServiceMembership _membership;

    public DirectoryMembershipSnapshot CurrentView { get; private set; } = DirectoryMembershipSnapshot.Default;

    public int PartitionsPerSilo => _membership.CurrentView.PartitionCount;

    public IAsyncEnumerable<DirectoryMembershipSnapshot> ViewUpdates => _viewUpdates;

    public IClusterMembershipService ClusterMembershipService => _membership.ClusterMembershipService;

    public async ValueTask<DirectoryMembershipSnapshot> RefreshViewAsync(MembershipVersion version, CancellationToken cancellationToken)
    {
        if (version == default || CurrentView.Version < version)
        {
            await ClusterMembershipService.Refresh(version, cancellationToken);
        }

        if (CurrentView.Version < version)
        {
            await foreach (var view in _viewUpdates.WithCancellation(cancellationToken))
            {
                if (view.Version >= version)
                {
                    break;
                }
            }
        }

        return CurrentView;
    }

    public DirectoryMembershipService(
        IClusterMembershipService clusterMembershipService,
        IInternalGrainFactory grainFactory,
        ILogger<DirectoryMembershipService> logger,
        int partitionsPerSilo,
        Func<SiloAddress, int, uint[]> getRingBoundaries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionsPerSilo, 1);
        _membership = new(
            clusterMembershipService,
            DirectoryMembershipSnapshot.CreateConfiguration(partitionsPerSilo),
            getRingBoundaries,
            logger,
            ClusterMembershipSnapshot.Default);
        CurrentView = new(_membership.CurrentView, grainFactory);
        _viewUpdates = new(
            CurrentView,
            (previous, proposed) => proposed.Version > previous.Version,
            update => CurrentView = update);
        _grainFactory = grainFactory;
        _logger = logger;
        using var _ = new ExecutionContextSuppressor();
        _runTask = Task.Run(ProcessMembershipUpdates);
    }

    private async Task ProcessMembershipUpdates()
    {
        try
        {
            while (!_shutdownCts.IsCancellationRequested)
            {
                try
                {
                    await foreach (var update in _membership.ViewUpdates.WithCancellation(_shutdownCts.Token))
                    {
                        _viewUpdates.TryPublish(new(update, _grainFactory));
                    }

                    break;
                }
                catch (Exception exception)
                {
                    if (!_shutdownCts.IsCancellationRequested)
                    {
                        LogErrorProcessingMembershipUpdates(exception);
                    }
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
        await _membership.DisposeAsync();
        _shutdownCts.Dispose();
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing membership updates."
    )]
    private partial void LogErrorProcessingMembershipUpdates(Exception exception);
}
