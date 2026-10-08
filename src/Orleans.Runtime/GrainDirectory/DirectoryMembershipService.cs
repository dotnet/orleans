using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
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
    private readonly int _partitionsPerSilo;
    private readonly Func<SiloAddress, int, ImmutableArray<uint>> _getRingBoundaries;
    private DirectoryMembershipSnapshot _currentView = DirectoryMembershipSnapshot.Default;

    public DirectoryMembershipSnapshot CurrentView
    {
        get
        {
            if (_runTask.IsFaulted)
            {
                _runTask.GetAwaiter().GetResult();
            }

            return _currentView;
        }
    }

    public int PartitionsPerSilo => _partitionsPerSilo;

    public IAsyncEnumerable<DirectoryMembershipSnapshot> ViewUpdates => GetViewUpdates();

    public IClusterMembershipService ClusterMembershipService { get; }

    public async ValueTask<DirectoryMembershipSnapshot> RefreshViewAsync(MembershipVersion version, CancellationToken cancellationToken)
    {
        if (CurrentView.Version < version || version == default)
        {
            await ClusterMembershipService.Refresh(version, cancellationToken);
        }

        if (CurrentView.Version < version)
        {
            await foreach (var view in ViewUpdates.WithCancellation(cancellationToken))
            {
                if (view.Version >= version)
                {
                    break;
                }
            }
        }

        return CurrentView;
    }

    private async IAsyncEnumerable<DirectoryMembershipSnapshot> GetViewUpdates([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var view in _viewUpdates.WithCancellation(cancellationToken))
        {
            yield return view;
        }

        // Propagate the processor's failure to current and future subscribers.
        await _runTask;
    }

    public DirectoryMembershipService(
        IClusterMembershipService clusterMembershipService,
        IInternalGrainFactory grainFactory,
        ILogger<DirectoryMembershipService> logger,
        int partitionsPerSilo,
        Func<SiloAddress, int, ImmutableArray<uint>> getRingBoundaries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partitionsPerSilo, 1);
        ArgumentNullException.ThrowIfNull(getRingBoundaries);
        _partitionsPerSilo = partitionsPerSilo;
        _getRingBoundaries = getRingBoundaries;
        _viewUpdates = new(
            DirectoryMembershipSnapshot.Default,
            (previous, proposed) => proposed.Version >= previous.Version,
            update => _currentView = update);
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
                        DirectoryMembershipSnapshot view;
                        try
                        {
                            view = new DirectoryMembershipSnapshot(update, _grainFactory, _partitionsPerSilo, _getRingBoundaries);
                        }
                        catch (Exception exception)
                        {
                            throw new OrleansConfigurationException(
                                $"Failed to construct grain directory membership version '{update.Version}' using the configured partition boundaries.",
                                exception);
                        }

                        _viewUpdates.Publish(view);
                    }
                }
                catch (Exception exception) when (exception is not OrleansConfigurationException)
                {
                    if (!_shutdownCts.IsCancellationRequested)
                    {
                        LogErrorProcessingMembershipUpdates(exception);
                    }
                }
            }
        }
        catch (OrleansConfigurationException exception)
        {
            if (!_shutdownCts.IsCancellationRequested)
            {
                LogErrorProcessingMembershipUpdates(exception);
            }

            throw;
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
