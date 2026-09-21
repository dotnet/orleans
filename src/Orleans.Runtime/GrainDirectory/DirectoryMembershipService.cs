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
    private readonly MembershipBasedClusterServiceViewProvider _membership;
    private readonly bool _ownsViewProvider;

    public DirectoryMembershipSnapshot CurrentView { get; private set; } = DirectoryMembershipSnapshot.Default;

    public int PartitionsPerSilo => _membership.CurrentView.Topology.PartitionCount;

    public IAsyncEnumerable<DirectoryMembershipSnapshot> ViewUpdates => _viewUpdates;

    public IClusterMembershipService ClusterMembershipService => _membership.ClusterMembershipService;

    public async ValueTask<DirectoryMembershipSnapshot> RefreshViewAsync(MembershipVersion version, CancellationToken cancellationToken)
    {
        if (version != default && CurrentView.Version >= version)
        {
            return CurrentView;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownCts.Token);
        await _membership.RefreshViewAsync(
            version == default ? null : DirectoryMembershipSnapshot.GetViewId(version),
            linkedCts.Token);
        if (CurrentView.Version < version)
        {
            await foreach (var view in _viewUpdates.WithCancellation(linkedCts.Token))
            {
                if (view.Version >= version)
                {
                    return view;
                }
            }

            throw new OperationCanceledException(
                "Directory membership updates completed before the requested view was published.",
                linkedCts.Token);
        }

        return CurrentView;
    }

    public DirectoryMembershipService(
        IClusterMembershipService clusterMembershipService,
        IInternalGrainFactory grainFactory,
        ILogger<DirectoryMembershipService> logger,
        int partitionsPerSilo,
        Func<SiloAddress, int, uint[]> getRingBoundaries)
        : this(
            new MembershipBasedClusterServiceViewProvider(
                clusterMembershipService,
                DirectoryMembershipSnapshot.CreateConfiguration(partitionsPerSilo),
                getRingBoundaries,
                logger,
                ClusterMembershipSnapshot.Default),
            grainFactory,
            logger,
            ownsViewProvider: true)
    {
    }

    public DirectoryMembershipService(
        IClusterServiceViewProvider viewProvider,
        IInternalGrainFactory grainFactory,
        ILogger<DirectoryMembershipService> logger)
        : this(viewProvider, grainFactory, logger, ownsViewProvider: false)
    {
    }

    private DirectoryMembershipService(
        IClusterServiceViewProvider viewProvider,
        IInternalGrainFactory grainFactory,
        ILogger<DirectoryMembershipService> logger,
        bool ownsViewProvider)
    {
        ArgumentNullException.ThrowIfNull(viewProvider);
        _membership = viewProvider as MembershipBasedClusterServiceViewProvider
            ?? throw new ArgumentException(
                "The distributed directory requires a membership-derived view provider for its membership-version wire contract.",
                nameof(viewProvider));
        _ownsViewProvider = ownsViewProvider;
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
            _shutdownCts.Cancel();
            _viewUpdates.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        await _runTask.SuppressThrowing();
        if (_ownsViewProvider)
        {
            await _membership.DisposeAsync();
        }
        _shutdownCts.Dispose();
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing membership updates."
    )]
    private partial void LogErrorProcessingMembershipUpdates(Exception exception);
}
