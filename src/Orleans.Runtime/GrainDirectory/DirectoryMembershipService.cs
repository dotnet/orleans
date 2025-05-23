using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Utilities;

#nullable enable
namespace Orleans.Runtime.GrainDirectory;

internal sealed partial class DirectoryMembershipService : IAsyncDisposable
{
    private readonly IInternalGrainFactory _grainFactory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _runTask;
    private readonly AsyncEnumerable<DirectoryMembershipSnapshot> _viewUpdates;

    public DirectoryMembershipSnapshot CurrentView { get; private set; } = DirectoryMembershipSnapshot.Default;

    public IAsyncEnumerable<DirectoryMembershipSnapshot> ViewUpdates => _viewUpdates;

    public ClusterMembershipService ClusterMembershipService { get; }

    public async ValueTask<DirectoryMembershipSnapshot> RefreshViewAsync(MembershipVersion version, CancellationToken cancellationToken)
    {
        _ = ClusterMembershipService.Refresh(version, cancellationToken);
        if (CurrentView.Version <= version)
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

    public DirectoryMembershipService(ClusterMembershipService clusterMembershipService, IInternalGrainFactory grainFactory, ILogger<DirectoryMembershipService> logger)
    {
        _viewUpdates = new(
            DirectoryMembershipSnapshot.Default,
            (previous, proposed) => proposed.Version >= previous.Version,
            update => CurrentView = update);
        ClusterMembershipService = clusterMembershipService;
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
                    await foreach (var update in ClusterMembershipService.MembershipUpdates.WithCancellation(_shutdownCts.Token))
                    {
                        var view = new DirectoryMembershipSnapshot(update, _grainFactory);
                        _viewUpdates.Publish(view);
                    }
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
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing membership updates."
    )]
    private partial void LogErrorProcessingMembershipUpdates(Exception exception);
}
