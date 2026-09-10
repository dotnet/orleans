namespace Orleans.Runtime.ClusterServices;

internal sealed record ResourceHandoffRequest(
    string Resource,
    SiloAddress Destination,
    RegisteredServiceViewId PreviousView,
    RegisteredServiceViewId TargetView,
    Guid ReceiverId);

internal sealed record ResourceHandoffState(
    RegisteredServiceViewId PreviousView,
    RegisteredServiceViewId TargetView,
    Guid ReceiverId,
    ReadOnlyMemory<byte> State);

/// <summary>
/// Service-specific durable state, transport, and external fencing establish each resource's authority to act.
/// Implementations must provide checkpoint/replay and external-effect guarantees appropriate to their resource.
/// </summary>
internal interface IResourceOwnershipProtocol
{
    ValueTask<ReadOnlyMemory<byte>> RecoverAsync(string resource, RegisteredServiceViewId targetView, CancellationToken cancellationToken);
    ValueTask<ResourceHandoffState> RequestHandoffAsync(SiloAddress source, ResourceHandoffRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Persists state under the recipient's ownership fence for <paramref name="previousView"/>.
    /// A superseded writer's checkpoint must preserve the current owner's durable state.
    /// </summary>
    ValueTask CheckpointAsync(string resource, ReadOnlyMemory<byte> state, RegisteredServiceViewId previousView, CancellationToken cancellationToken);
    ValueTask<ClusterServiceFence> AcquireFenceAsync(string resource, RegisteredServiceViewId targetView, CancellationToken cancellationToken);
}

/// <summary>
/// Coordinates finite and hash-ring resources through service-scoped immutable ownership views.
/// Admission, receiver identity, draining, state installation, and fencing exercise the production typed gates.
/// </summary>
internal sealed class ResourceOwnershipConsumer : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly SiloAddress _local;
    private readonly RegisteredClusterServiceViewProvider _provider;
    private readonly IResourceOwnershipProtocol _protocol;
    private readonly ResourceTransitionGateMap<string, RegisteredServiceViewId> _gates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Receiver> _receivers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RetainedState> _retained = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<Task> _work = [];
    private TaskCompletionSource _viewChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RegisteredClusterServiceView? _view;
    private Task _installation = Task.CompletedTask;
    private int _operations;
    private TaskCompletionSource? _operationsDrained;
    private bool _disposed;

    public ResourceOwnershipConsumer(SiloAddress local, RegisteredClusterServiceViewProvider provider, IResourceOwnershipProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(protocol);
        _local = local;
        _provider = provider;
        _protocol = protocol;
    }

    /// <summary>
    /// Registers every gate synchronously before exposing the new local ownership decision.
    /// A caller may cancel waiting on the returned task without cancelling shared transitions.
    /// </summary>
    public Task InstallViewAsync(RegisteredClusterServiceView view)
    {
        List<Func<Task>> work = [];
        TaskCompletionSource completion;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateProviderView(view.Id);
            if (_view is { } installed && view.Id.CompareTo(installed.Id) <= 0)
            {
                if (view.Id == installed.Id && installed.HasSameContent(view))
                {
                    return _installation;
                }

                throw new InvalidOperationException($"Cannot install '{view.Id}' over '{installed.Id}'.");
            }

            var previous = _view;
            var previousId = previous?.Id ?? new(view.Id.ServiceId, view.Id.AuthorityId, -1);
            var continuous = previous is not null && view.TryGetPredecessor(out var predecessor)
                && predecessor == previous.Id && previous.Configuration == view.Configuration;
            var newOwned = view.GetOwnedResources(_local);
            // Advancing the local view makes every earlier handoff target obsolete.
            // In-flight releases keep their retained receiver alive through their own reference.
            _retained.Clear();
            if (previous is not null)
            {
                // Only local owned sets are traversed: O(oldOwned + newOwned), not a global product.
                foreach (var resource in previous.GetOwnedResources(_local))
                {
                    if (!newOwned.Contains(resource))
                    {
                        var gate = new OwnershipRelease<RegisteredServiceViewId>(previousId, view.Id);
                        _gates.Add(resource, gate);
                        var retained = new RetainedState(_receivers[resource], gate);
                        _retained[resource] = retained;
                        _receivers.Remove(resource);
                        work.Add(() => ReleaseAsync(resource, retained));
                    }
                }
            }

            foreach (var resource in newOwned)
            {
                var resourceContinuous = continuous
                    && previous!.ResourceRanges.TryGetValue(resource, out var previousRange) == view.ResourceRanges.TryGetValue(resource, out var targetRange)
                    && previousRange.Equals(targetRange);
                if (resourceContinuous && previous!.GetOwnedResources(_local).Contains(resource)
                    && _receivers[resource].Ready && !_gates.IsBlocked(resource, previous.Id))
                {
                    _receivers[resource].View = view.Id;
                    continue;
                }

                _receivers.TryGetValue(resource, out var oldReceiver);
                var receiver = new Receiver(view.Id);
                _receivers[resource] = receiver;
                var gate = new OwnershipAcquisition<RegisteredServiceViewId>(previousId, view.Id);
                _gates.Add(resource, gate);
                work.Add(() => AcquireAsync(resource, receiver, oldReceiver, previous, view, resourceContinuous, gate));
            }

            _view = view;
            var changed = _viewChanged;
            _viewChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _installation = completion.Task;
            _work.RemoveWhere(static task => task.IsCompleted);
            _work.Add(completion.Task);
        }

        _ = CompleteInstallationAsync(work, completion);
        return completion.Task;
    }

    private static async Task CompleteInstallationAsync(List<Func<Task>> work, TaskCompletionSource completion)
    {
        try
        {
            await Task.WhenAll(work.Select(static start => start()));
            completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    /// <summary>
    /// Routes a hash point through the authoritative ring's stable resource identity and receiver admission.
    /// </summary>
    public ValueTask<ReadOnlyMemory<byte>> ExecuteRingAsync(
        uint hashCode,
        RegisteredServiceViewId requestView,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> operation,
        CancellationToken cancellationToken)
    {
        string resource;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_view is null || _view.Id != requestView
                || !_view.TryGetRingResource(hashCode, out var routedResource, out var owner) || !owner.Equals(_local))
            {
                throw new ClusterServiceViewUnavailableException($"Ring point '{hashCode}' is not available on '{_local}' in '{requestView}'.");
            }

            resource = routedResource;
        }

        return ExecuteAsync(resource, requestView, operation, cancellationToken);
    }

    /// <summary>
    /// Serializes state transformations for each receiver, revalidating ownership, gates, and receiver
    /// identity after admission waits and before committing the result.
    /// </summary>
    public async ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(
        string resource,
        RegisteredServiceViewId requestView,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Receiver receiver;
        lock (_lock)
        {
            ValidateOwnership(resource, requestView);
            receiver = _receivers[resource];
            _operations++;
        }

        try
        {
            return await ExecuteCoreAsync(resource, requestView, receiver, operation, cancellationToken);
        }
        finally
        {
            lock (_lock)
            {
                if (--_operations == 0)
                {
                    _operationsDrained?.TrySetResult();
                }
            }
        }
    }

    private async ValueTask<ReadOnlyMemory<byte>> ExecuteCoreAsync(
        string resource,
        RegisteredServiceViewId requestView,
        Receiver receiver,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> operation,
        CancellationToken cancellationToken)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        while (_gates.TryGetBlockingTransition(resource, requestView, out var blocker))
        {
            try
            {
                await blocker.WaitAsync(operationCancellation.Token);
            }
            catch (OperationCanceledException) when (blocker.IsCanceled
                && !cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
            {
                // Superseded acquisitions release their gate; admission rechecks the receiver and map.
            }

            lock (_lock)
            {
                ValidateReceiver(resource, receiver, requestView);
            }
        }

        await receiver.Operations.WaitAsync(operationCancellation.Token);
        try
        {
            operationCancellation.Token.ThrowIfCancellationRequested();
            ReadOnlyMemory<byte> state;
            lock (_lock)
            {
                ValidateReceiver(resource, receiver, requestView);
                if (!receiver.Ready || _gates.IsBlocked(resource, requestView))
                {
                    throw new ClusterServiceViewUnavailableException($"Resource '{resource}' is gated in '{requestView}'.");
                }

                receiver.Active++;
                state = receiver.State;
            }

            try
            {
                var next = await operation(state, operationCancellation.Token);
                operationCancellation.Token.ThrowIfCancellationRequested();
                lock (_lock)
                {
                    // Delayed old-view requests cannot overwrite a new receiver or a newer ownership view.
                    ValidateReceiver(resource, receiver, requestView);
                    if (_gates.IsBlocked(resource, requestView))
                    {
                        throw new ClusterServiceViewUnavailableException($"Resource '{resource}' became gated in '{requestView}'.");
                    }

                    receiver.State = next.ToArray();
                    return receiver.State;
                }
            }
            finally
            {
                lock (_lock)
                {
                    if (--receiver.Active == 0)
                    {
                        receiver.Drained?.TrySetResult();
                    }
                }
            }
        }
        finally
        {
            receiver.Operations.Release();
        }
    }

    public async ValueTask<ResourceHandoffState> CreateHandoffAsync(ResourceHandoffRequest request, CancellationToken cancellationToken)
    {
        RetainedState retained;
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ValidateProviderView(request.TargetView);
                if (_view is { } current && current.Id.CompareTo(request.TargetView) >= 0)
                {
                    retained = ValidateHandoff(request);
                    break;
                }

                changed = _viewChanged.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }

        await retained.Gate.Completion.WaitAsync(cancellationToken);
        lock (_lock)
        {
            if (!ReferenceEquals(retained, ValidateHandoff(request)))
            {
                throw new ClusterServiceViewUnavailableException("The retained source state was superseded while awaiting handoff.");
            }

            return new(request.PreviousView, request.TargetView, request.ReceiverId, retained.Receiver.State.ToArray());
        }
    }

    private RetainedState ValidateHandoff(ResourceHandoffRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateProviderView(request.TargetView);
        if (_view is null || _view.Id != request.TargetView
            || !_view.TryGetPredecessor(out var predecessor) || predecessor != request.PreviousView
            || !_view.ResourceOwners.TryGetValue(request.Resource, out var destination) || !destination.Equals(request.Destination)
            || !_provider.IsOwnerLive(destination, _view.MembershipWatermark)
            || !_retained.TryGetValue(request.Resource, out var retained)
            || retained.Gate.PreviousView != request.PreviousView || retained.Gate.TargetView != request.TargetView)
        {
            throw new ClusterServiceViewUnavailableException($"Stale or unproven handoff for '{request.Resource}' at '{request.TargetView}'.");
        }

        return retained;
    }

    private async Task AcquireAsync(
        string resource,
        Receiver receiver,
        Receiver? oldReceiver,
        RegisteredClusterServiceView? previous,
        RegisteredClusterServiceView target,
        bool continuous,
        OwnershipAcquisition<RegisteredServiceViewId> gate)
    {
        try
        {
            await WaitForPredecessorAsync(resource, gate.PreviousView);
            EnsureCurrent(resource, receiver, target.Id);
            if (oldReceiver is { Ready: true })
            {
                await DrainAsync(oldReceiver);
                EnsureCurrent(resource, receiver, target.Id);
                await _protocol.CheckpointAsync(resource, oldReceiver.State, gate.PreviousView, _shutdown.Token);
                EnsureCurrent(resource, receiver, target.Id);
            }

            ReadOnlyMemory<byte> state;
            if (continuous && previous!.ResourceOwners.TryGetValue(resource, out var source)
                && !source.Equals(_local) && _provider.IsOwnerLive(source, target.MembershipWatermark))
            {
                var request = new ResourceHandoffRequest(resource, _local, previous.Id, target.Id, receiver.Id);
                try
                {
                    var response = await _protocol.RequestHandoffAsync(source, request, _shutdown.Token);
                    EnsureCurrent(resource, receiver, target.Id);
                    if (response.PreviousView != request.PreviousView || response.TargetView != request.TargetView || response.ReceiverId != receiver.Id)
                    {
                        throw new InvalidOperationException($"Snapshot response does not match receiver '{receiver.Id}' in '{target.Id}'.");
                    }

                    state = response.State;
                }
                catch (ClusterServiceViewUnavailableException)
                {
                    EnsureCurrent(resource, receiver, target.Id);
                    // This reader has continuity, but the source might have skipped its ownership view.
                    state = await _protocol.RecoverAsync(resource, target.Id, _shutdown.Token);
                    EnsureCurrent(resource, receiver, target.Id);
                }
            }
            else
            {
                state = await _protocol.RecoverAsync(resource, target.Id, _shutdown.Token);
                EnsureCurrent(resource, receiver, target.Id);
            }

            lock (_lock)
            {
                ValidateReceiver(resource, receiver, target.Id);
                receiver.State = state.ToArray();
                gate.MarkStateInstalled();
            }

            var fence = await _protocol.AcquireFenceAsync(resource, target.Id, _shutdown.Token);
            lock (_lock)
            {
                ValidateReceiver(resource, receiver, target.Id);
                if (fence.Mode is not ClusterServiceFencingMode.External)
                {
                    throw new InvalidOperationException("A resource receiver requires its external provider fence, not a placement or membership revision.");
                }

                gate.MarkFenced(fence);
                receiver.Ready = true;
                gate.Complete();
                _gates.Prune(resource);
            }
        }
        catch (Exception exception)
        {
            lock (_lock)
            {
                if (gate.Status is TransitionGateStatus.Pending
                    && (_view?.Id != target.Id || !_receivers.TryGetValue(resource, out var current) || !ReferenceEquals(current, receiver)))
                {
                    gate.Abort();
                    _gates.Prune(resource);
                }
                else
                {
                    FinishFailure(resource, gate, exception);
                }
            }
        }

        await gate.Completion;
    }

    private async Task ReleaseAsync(string resource, RetainedState retained)
    {
        var gate = retained.Gate;
        try
        {
            // The target's outbound gate must not wait on itself.
            await WaitForPredecessorAsync(resource, gate.PreviousView);
            await DrainAsync(retained.Receiver);
            ValidateProviderView(gate.TargetView);
            if (!retained.Receiver.Ready)
            {
                throw new ClusterServiceViewUnavailableException($"No installed predecessor state exists for '{resource}' in '{gate.PreviousView}'.");
            }

            gate.MarkDrained();
            await _protocol.CheckpointAsync(resource, retained.Receiver.State, gate.PreviousView, _shutdown.Token);
            ValidateProviderView(gate.TargetView);
            gate.MarkStateRetained();
            gate.Complete();
            _gates.Prune(resource);
        }
        catch (Exception exception)
        {
            FinishFailure(resource, gate, exception);
        }

        await gate.Completion;
    }

    private async Task WaitForPredecessorAsync(string resource, RegisteredServiceViewId previous)
    {
        while (_gates.TryGetBlockingTransition(resource, previous, out var blocker))
        {
            try
            {
                await blocker.WaitAsync(_shutdown.Token);
            }
            catch (OperationCanceledException) when (blocker.IsCanceled && !_shutdown.IsCancellationRequested)
            {
                // A superseded acquisition can abort while a newer transition waits on it.
                // Re-query the map: failed gates still fault, but aborted gates no longer block.
            }
        }
    }

    private Task DrainAsync(Receiver receiver)
    {
        lock (_lock)
        {
            return receiver.Active == 0 ? Task.CompletedTask
                : (receiver.Drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(_shutdown.Token);
        }
    }

    private void EnsureCurrent(string resource, Receiver receiver, RegisteredServiceViewId view)
    {
        lock (_lock)
        {
            ValidateReceiver(resource, receiver, view);
        }
    }

    private void ValidateOwnership(string resource, RegisteredServiceViewId view)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateProviderView(view);
        if (_view is null || _view.Id != view
            || !_view.ResourceOwners.TryGetValue(resource, out var owner) || !owner.Equals(_local)
            || !_provider.IsOwnerLive(_local, _view.MembershipWatermark))
        {
            throw new ClusterServiceViewUnavailableException($"Resource '{resource}' is not available on '{_local}' in '{view}'.");
        }
    }

    private void ValidateProviderView(RegisteredServiceViewId view)
    {
        _provider.ValidateAuthority(view);
        if (!_provider.TryGetCurrentView(out var current))
        {
            throw new ClusterServiceViewUnavailableException($"Service authority for '{view}' is unavailable.");
        }

        current.Id.CompareTo(view);
    }

    private void ValidateReceiver(string resource, Receiver receiver, RegisteredServiceViewId view)
    {
        ValidateOwnership(resource, view);
        if (!_receivers.TryGetValue(resource, out var current) || !ReferenceEquals(current, receiver) || receiver.View != view)
        {
            throw new ClusterServiceViewUnavailableException($"Receiver '{receiver.Id}' for '{resource}' in '{view}' was superseded.");
        }
    }

    private void FinishFailure(string resource, TransitionGate<RegisteredServiceViewId> gate, Exception exception)
    {
        lock (_lock)
        {
            if (gate.Status is TransitionGateStatus.Pending)
            {
                if (_shutdown.IsCancellationRequested)
                {
                    gate.Abort(_shutdown.Token);
                }
                else
                {
                    gate.Fail(exception);
                }
            }

            _gates.Prune(resource);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] work;
        Task operationsDrained;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            operationsDrained = _operations == 0 ? Task.CompletedTask
                : (_operationsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            _shutdown.Cancel();
            _viewChanged.TrySetResult();
            _gates.AbortAll(_shutdown.Token);
            _retained.Clear();
            work = _work.ToArray();
        }

        try
        {
            await Task.WhenAll(work);
        }
        catch
        {
            // Each transition's original exception remains available through its returned task.
        }

        await operationsDrained;
    }

    private sealed class Receiver(RegisteredServiceViewId view)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public RegisteredServiceViewId View { get; set; } = view;
        public ReadOnlyMemory<byte> State { get; set; }
        public SemaphoreSlim Operations { get; } = new(1, 1);
        public bool Ready { get; set; }
        public int Active { get; set; }
        public TaskCompletionSource? Drained { get; set; }
    }

    private sealed record RetainedState(Receiver Receiver, OwnershipRelease<RegisteredServiceViewId> Gate);
}
