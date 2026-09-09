using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Orleans.Runtime.ClusterServices;

/// <summary>
/// A per-service CAS authority. Participation, configuration, and assignments change only in the same publication.
/// Membership can independently invalidate a transfer partner, but cannot silently reassign its resources.
/// </summary>
internal sealed class RegisteredClusterServiceViewProvider : IClusterServiceViewProvider<RegisteredServiceViewId, RegisteredClusterServiceView>
{
    private readonly object _lock = new();
    private readonly IClusterServiceViewRegister _register;
    private readonly IClusterMembershipService _membership;
    private readonly Func<ClusterMember, bool> _eligible;
    private readonly RegisteredServiceViewId _namespace;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _reader = new(1);
    private readonly Task _polling;
    private TaskCompletionSource _changed = NewSignal();
    private RegisteredClusterServiceView? _current;
    private ClusterServiceRegisterRead? _lastRead;
    private Exception? _terminal;
    private bool _disposed;

    public RegisteredClusterServiceViewProvider(
        string serviceId,
        string authorityId,
        IClusterServiceViewRegister register,
        IClusterMembershipService membership,
        Func<ClusterMember, bool>? eligible = null,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(membership);
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _namespace = new(serviceId, authorityId, 0);
        _register = register;
        _membership = membership;
        _eligible = eligible ?? (static _ => true);
        _polling = PollAsync(interval);
    }

    public IAsyncEnumerable<RegisteredClusterServiceView> ViewUpdates => ReadUpdates();

    public bool TryGetCurrentView([MaybeNullWhen(false)] out RegisteredClusterServiceView view)
    {
        lock (_lock)
        {
            view = _current!;
            return view is not null && _terminal is null;
        }
    }

    public ValueTask<RegisteredClusterServiceView> RefreshAsync(CancellationToken cancellationToken) =>
        RefreshCoreAsync(null, cancellationToken);

    public ValueTask<RegisteredClusterServiceView> RefreshAtLeastAsync(RegisteredServiceViewId minimumView, CancellationToken cancellationToken) =>
        RefreshCoreAsync(minimumView, cancellationToken);

    public ValueTask RefreshLivenessAsync(CancellationToken cancellationToken) =>
        _membership.Refresh(cancellationToken: cancellationToken);

    public bool IsOwnerLive(SiloAddress owner, MembershipVersion watermark) =>
        _membership.CurrentSnapshot.Version >= watermark && _membership.CurrentSnapshot.GetSiloStatus(owner) is SiloStatus.Active;

    /// <summary>
    /// Publishes against the actual register predecessor. A losing writer returns null without rebasing the proposal.
    /// All caller-owned collections are copied before the first write.
    /// </summary>
    public async ValueTask<RegisteredClusterServiceView?> TryPublishAsync(
        RegisteredServiceConfiguration configuration,
        IEnumerable<SiloAddress> participants,
        IEnumerable<string> resources,
        IEnumerable<KeyValuePair<string, SiloAddress>> assignments,
        CancellationToken cancellationToken,
        ImmutableArray<ClusterServicePartitionAssignment> ringAssignments = default)
    {
        ThrowIfTerminated();
        cancellationToken.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _reader.WaitAsync(linked.Token);
        try
        {
            return await TryPublishCoreAsync(configuration, participants, resources, assignments, linked.Token, ringAssignments);
        }
        finally
        {
            _reader.Release();
        }
    }

    private async ValueTask<RegisteredClusterServiceView?> TryPublishCoreAsync(
        RegisteredServiceConfiguration configuration,
        IEnumerable<SiloAddress> participants,
        IEnumerable<string> resources,
        IEnumerable<KeyValuePair<string, SiloAddress>> assignments,
        CancellationToken cancellationToken,
        ImmutableArray<ClusterServicePartitionAssignment> ringAssignments)
    {
        ClusterServiceRegisterRead read;
        try
        {
            read = await _register.ReadAsync(cancellationToken);
            ValidateRead(read);
            if (read.View is { } predecessor)
            {
                await _membership.Refresh(predecessor.MembershipWatermark, cancellationToken);
                if (_membership.CurrentSnapshot.Version < predecessor.MembershipWatermark)
                {
                    throw new ClusterServiceViewUnavailableException($"Membership has not reached the watermark for '{predecessor.Id}'.");
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Terminate(exception);
            throw;
        }

        var membership = _membership.CurrentSnapshot;
        var proposed = new RegisteredClusterServiceView(
            new(_namespace.ServiceId, _namespace.AuthorityId, checked((read.View?.Id.Revision ?? 0) + 1)),
            read.View?.Id,
            membership.Version,
            configuration,
            participants,
            resources,
            assignments,
            ringAssignments);
        foreach (var participant in proposed.Participants)
        {
            if (!membership.Members.TryGetValue(participant, out var member)
                || member.Status is not SiloStatus.Active || !_eligible(member))
            {
                throw new ArgumentException($"Participant '{participant}' is not eligible in membership {membership.Version}.", nameof(participants));
            }
        }

        bool written;
        try
        {
            written = await _register.TryWriteAsync(proposed, read.Token, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Terminate(exception);
            throw;
        }

        if (!written)
        {
            return null;
        }

        Install(proposed);
        return proposed;
    }

    private async ValueTask<RegisteredClusterServiceView> RefreshCoreAsync(RegisteredServiceViewId? minimum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (minimum is { } requested)
        {
            _namespace.CompareTo(requested);
        }

        ThrowIfTerminated();
        // A caller cancels its wait, never a shared register read or publication.
        var refresh = ReadAndInstallAsync();
        try
        {
            await refresh.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = ObserveAbandonedReadAsync(refresh);
            throw;
        }

        while (true)
        {
            Task changed;
            lock (_lock)
            {
                ThrowIfTerminated();
                if (_current is null)
                {
                    throw new ClusterServiceViewUnavailableException($"Service authority '{_namespace}' has not been initialized.");
                }

                if (minimum is null || _current.Id.CompareTo(minimum.Value) >= 0)
                {
                    return _current;
                }

                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }
    }

    private static async Task ObserveAbandonedReadAsync(Task read)
    {
        try
        {
            await read;
        }
        catch
        {
            // ReadAndInstallAsync publishes its failure to all remaining refreshes and subscribers.
        }
    }

    private async Task ReadAndInstallAsync()
    {
        await _reader.WaitAsync(_shutdown.Token);
        try
        {
            ThrowIfTerminated();
            var read = await _register.ReadAsync(_shutdown.Token);
            ValidateRead(read);
            if (read.View is { } view)
            {
                await _membership.Refresh(view.MembershipWatermark, _shutdown.Token);
                if (_membership.CurrentSnapshot.Version < view.MembershipWatermark)
                {
                    throw new ClusterServiceViewUnavailableException($"Membership has not reached the watermark for '{view.Id}'.");
                }

                Install(view);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !_shutdown.IsCancellationRequested)
        {
            Terminate(exception);
            throw;
        }
        finally
        {
            _reader.Release();
        }
    }

    private void ValidateRead(ClusterServiceRegisterRead read)
    {
        lock (_lock)
        {
            ThrowIfTerminated();
            var view = read.View;
            if (view is null)
            {
                if (_current is not null)
                {
                    throw new ClusterServiceAuthorityException($"Register '{_namespace}' was removed. Bootstrap a new authority namespace.");
                }

                return;
            }

            _namespace.CompareTo(view.Id);
            if (_lastRead is { View: { } previousRead } && view.Id == previousRead.Id
                && !StringComparer.Ordinal.Equals(read.Token, _lastRead.Token))
            {
                throw new ClusterServiceAuthorityException($"Register '{view.Id}' was rewritten without a new revision. Explicit bootstrap is required.");
            }

            if (_current is { } current)
            {
                if (view.Id.CompareTo(current.Id) < 0)
                {
                    throw new ClusterServiceAuthorityException($"Register '{view.Id}' regressed from '{current.Id}'. Explicit bootstrap is required.");
                }

                if (view.Id == current.Id && !current.HasSameContent(view))
                {
                    throw new ClusterServiceAuthorityException($"Canonical view identity '{view.Id}' has conflicting content.");
                }
            }

            _lastRead = read;
        }
    }

    private void Install(RegisteredClusterServiceView view)
    {
        lock (_lock)
        {
            ThrowIfTerminated();
            if (_current is { } current && view.Id.CompareTo(current.Id) <= 0)
            {
                if (view.Id == current.Id && !view.HasSameContent(current))
                {
                    throw new ClusterServiceAuthorityException($"Canonical view identity '{view.Id}' has conflicting content.");
                }

                return;
            }

            _current = view;
            Signal();
        }
    }

    private async Task PollAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token))
            {
                await ReadAndInstallAsync();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Terminate(exception);
        }
    }

    private async IAsyncEnumerable<RegisteredClusterServiceView> ReadUpdates([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RegisteredServiceViewId? last = null;
        while (true)
        {
            RegisteredClusterServiceView? next;
            Task changed;
            lock (_lock)
            {
                ThrowIfTerminated();
                next = _current is { } current && (last is null || current.Id.CompareTo(last.Value) > 0) ? current : null;
                changed = _changed.Task;
            }

            if (next is not null)
            {
                last = next.Id;
                yield return next;
            }
            else
            {
                await changed.WaitAsync(cancellationToken);
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Signal()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private void Terminate(Exception exception)
    {
        lock (_lock)
        {
            _terminal ??= exception;
            Signal();
        }
    }

    private void ThrowIfTerminated()
    {
        lock (_lock)
        {
            if (_terminal is { } exception)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Terminate(new ObjectDisposedException(nameof(RegisteredClusterServiceViewProvider)));
        }

        _shutdown.Cancel();
        await _polling;
        await _reader.WaitAsync();
        _reader.Release();
    }
}
