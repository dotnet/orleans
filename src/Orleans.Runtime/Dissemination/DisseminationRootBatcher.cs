using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Internal;
using KeyNotification = Orleans.Runtime.Dissemination.DisseminationBroadcastQueue.KeyNotification;

namespace Orleans.Runtime.Dissemination;

// Cohorts contain identities, not values. Peer queues still own payload materialization, byte
// limits, splitting, and RPC lifetime. A receipt acknowledges admission there, not delivery.
internal sealed partial class DisseminationRootBatcher
{
    internal delegate bool DispatchCallback(ReadOnlySpan<KeyNotification> notifications);

    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly IDisseminationNamespace _namespace;
    private readonly DisseminationNamespace _namespaceName;
    private readonly IOptionsMonitor<DisseminationOptions> _options;
    private readonly Func<DisseminationMembershipSnapshot> _getMembership;
    private readonly DispatchCallback _dispatch;
    private readonly ILogger _logger;
    private readonly WakeTimer _timer;
    private readonly Task _worker;
    private readonly Dictionary<DisseminationKey, PendingKey> _pending = [];
    private readonly LinkedList<PendingKey> _ready = [];
    private readonly Dictionary<SiloAddress, Producer> _producers = [];
    private DisseminationMembershipSnapshot? _membership;
    private Cohort? _cohort;
    private TaskCompletionSource? _drainCompletion;
    private Exception? _failure;
    private long? _retryTimestamp;
    private TimeSpan _retryPeriod;
    private bool _dispatching;
    private bool _workerActive;
    private bool _handoff;
    private bool _stopping;
    private bool _aborted;
    private CancellationToken _abortToken;

    public DisseminationRootBatcher(
        TimeProvider timeProvider,
        IDisseminationNamespace disseminationNamespace,
        IOptionsMonitor<DisseminationOptions> options,
        Func<DisseminationMembershipSnapshot> getMembership,
        DispatchCallback dispatch,
        ILogger logger)
    {
        _timeProvider = timeProvider;
        _namespace = disseminationNamespace;
        _namespaceName = disseminationNamespace.Name;
        _options = options;
        _getMembership = getMembership;
        _dispatch = dispatch;
        _logger = logger;
        _timer = new(timeProvider);
        using var _ = new ExecutionContextSuppressor();
        _worker = RunAsync();
    }

    // Hints (including owner inventory and forwarded values) never count as fresh contributions.
    public bool Notify(ReadOnlySpan<KeyNotification> notifications)
    {
        var settings = GetSettings();
        var membership = _getMembership();
        var rejected = 0;
        var wake = false;
        lock (_lock)
        {
            ThrowIfFailedUnsafe();
            if (_stopping)
            {
                return false;
            }

            membership = RefreshMembershipUnsafe(membership);
            if (!settings.Enabled)
            {
                ClearPendingUnsafe();
                wake = true;
                rejected = notifications.Length;
            }
            else
            {
                var wasEmpty = _ready.Count == 0;
                foreach (var notification in notifications)
                {
                    if (!IsEligible(notification, membership)
                        || !TryAddUnsafe(notification, settings, membership, out _))
                    {
                        rejected++;
                    }
                }

                wake = _ready.Count > 0 && wasEmpty;
            }
        }

        if (wake)
        {
            Wake();
        }

        if (rejected > 0)
        {
            ReportRejection(rejected, settings.MaxPendingItemCount);
        }

        return settings.Enabled && rejected == 0;
    }

    public ValueTask<DisseminationPublicationReceipt> PublishAsync(
        KeyNotification notification, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = GetSettings();
        var membership = _getMembership();
        Task<ReceiptOutcome> completion;
        var wake = false;
        lock (_lock)
        {
            ThrowIfFailedUnsafe();
            membership = RefreshMembershipUnsafe(membership);
            if (_stopping || !settings.Enabled || !membership.IsAggregationRoot
                || notification.Version <= 0
                || notification.Key.Value is not SiloAddress silo || !membership.ContainsMember(silo))
            {
                return new(new DisseminationPublicationReceipt(false, settings.Period));
            }

            if (_producers.TryGetValue(silo, out var producer) && notification.Version <= producer.Version)
            {
                // A retry must neither contribute again nor start another publication period.
                // Force on a retry is not a new invalidation; independent invalidations use Notify.
                if (_pending.TryGetValue(notification.Key, out var retained))
                {
                    retained.AdmissionGeneration++;
                }

                if (notification.Version <= producer.CompletedVersion)
                {
                    return new(CreateReceipt(producer.Outcome));
                }

                completion = notification.Version <= producer.InFlightVersion && producer.InFlightReceipt is { } inFlight
                    ? inFlight.Task : retained!.Receipt!.Task;
            }
            else
            {
                var wasEmpty = _ready.Count == 0;
                if (!TryAddUnsafe(notification, settings, membership, out var pending, contribution: true))
                {
                    return new(new DisseminationPublicationReceipt(false, settings.Period));
                }

                producer ??= new();
                _producers[silo] = producer;
                producer.Version = notification.Version;
                // All newer versions before sealing share one signal and occupy one producer slot.
                pending.Receipt ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Producer = producer;
                if (_cohort!.Membership.ContainsMember(silo))
                {
                    _cohort.Contributors.Add(silo);
                }

                completion = pending.Receipt.Task;
                wake = wasEmpty
                    || _cohort.Contributors.Count == _cohort.Membership.Members.Length;
            }
        }

        if (wake)
        {
            Wake();
        }

        return AwaitReceiptAsync(completion, cancellationToken);
    }

    private async ValueTask<DisseminationPublicationReceipt> AwaitReceiptAsync(
        Task<ReceiptOutcome> completion, CancellationToken cancellationToken)
    {
        // Cancellation detaches this RPC only. Other callers and the admitted contribution survive.
        var outcome = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            ThrowIfFailedUnsafe();
        }

        return CreateReceipt(outcome);
    }

    private DisseminationPublicationReceipt CreateReceipt(ReceiptOutcome outcome)
    {
        var delay = outcome.Period - _timeProvider.GetElapsedTime(outcome.Started);
        return new(outcome.Accepted, delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task completion;
        lock (_lock)
        {
            ThrowIfFailedUnsafe();
            ThrowIfAbortedUnsafe();
            if (_pending.Count == 0 && !_dispatching)
            {
                return;
            }

            _drainCompletion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _drainCompletion.Task;
        }

        Wake();
        await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            ThrowIfFailedUnsafe();
            ThrowIfAbortedUnsafe();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _stopping = true;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Wake();
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_lock)
            {
                _aborted = true;
                _abortToken = cancellationToken;
                ClearPendingUnsafe();
                CompleteDrainUnsafe();
            }

            DisposeTimer();
            throw;
        }

        lock (_lock)
        {
            ThrowIfFailedUnsafe();
            ThrowIfAbortedUnsafe();
        }
    }

    public void Prune(IReadOnlySet<DisseminationKey> activeKeys)
    {
        List<(DisseminationKey Key, PendingKey Pending, long Generation)> candidates = [];
        lock (_lock)
        {
            foreach (var (key, pending) in _pending)
            {
                if (!activeKeys.Contains(key))
                {
                    candidates.Add((key, pending, pending.AdmissionGeneration));
                }
            }
        }

        // Namespace callbacks may reenter and may block. Reconcile against admission generations.
        foreach (var candidate in candidates)
        {
            if (_namespace.GetVersion(candidate.Key) > 0)
            {
                continue;
            }

            lock (_lock)
            {
                if (_pending.TryGetValue(candidate.Key, out var pending)
                    && ReferenceEquals(pending, candidate.Pending)
                    && pending.AdmissionGeneration == candidate.Generation)
                {
                    RemoveUnsafe(pending);
                }
            }
        }

        Wake();
    }

    public void WakeForMembershipChange()
    {
        var membership = _getMembership();
        lock (_lock)
        {
            RefreshMembershipUnsafe(membership);
            // A new root is a different admission destination, so bypass an old child's retry floor.
            _handoff |= _ready.Count > 0;
        }

        Wake();
    }

    private bool TryAddUnsafe(
        KeyNotification notification, in Settings settings,
        DisseminationMembershipSnapshot membership, out PendingKey pending, bool contribution = false)
    {
        if (_pending.TryGetValue(notification.Key, out pending!))
        {
            pending.AdmissionGeneration++;
            if (!notification.Force && notification.Version <= pending.Notification.Version
                && (!contribution || pending.Node.List is not null))
            {
                return true;
            }

            pending.Notification = new(notification.Key,
                Math.Max(notification.Version, pending.Notification.Version),
                pending.Notification.Force || notification.Force);
            pending.Generation = pending.AdmissionGeneration;
        }
        else
        {
            if (_pending.Count >= settings.MaxPendingItemCount)
            {
                return false;
            }

            pending = new(notification);
            _pending.Add(notification.Key, pending);
        }

        _cohort ??= new(membership, _timeProvider.GetTimestamp(), settings.Period);
        if (pending.Node.List is null)
        {
            _ready.AddLast(pending.Node);
        }

        return true;
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _timer.WaitAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (true)
                {
                    var settings = GetSettings();
                    var membership = _getMembership();
                    PendingDispatch[] work;
                    Cohort cohort;
                    lock (_lock)
                    {
                        _workerActive = true;
                        if (_failure is not null || _aborted)
                        {
                            return;
                        }

                        membership = RefreshMembershipUnsafe(membership);
                        if (!settings.Enabled)
                        {
                            ClearPendingUnsafe();
                        }

                        if (_ready.Count == 0)
                        {
                            _cohort = null;
                            CompleteDrainUnsafe();
                            if (_stopping)
                            {
                                return;
                            }

                            _workerActive = false;
                            break;
                        }

                        var delay = GetDelayUnsafe();
                        if (delay > TimeSpan.Zero)
                        {
                            _timer.Change(delay);
                            _workerActive = false;
                            break;
                        }

                        cohort = _cohort!;
                        work = new PendingDispatch[_ready.Count];
                        for (var i = 0; i < work.Length; i++)
                        {
                            var pending = _ready.First!.Value;
                            _ready.RemoveFirst();
                            work[i] = new(pending, pending.Generation, pending.Notification,
                                pending.Receipt, pending.Producer, pending.Producer?.Version ?? 0);
                            if (pending.Producer is { } producer)
                            {
                                producer.InFlightVersion = producer.Version;
                                producer.InFlightReceipt = pending.Receipt;
                            }

                            pending.InFlight = pending.Receipt;
                            pending.Receipt = null;
                            pending.Producer = null;
                            pending.Notification = pending.Notification with { Force = false };
                        }

                        _cohort = null;
                        _handoff = false;
                        _retryTimestamp = null;
                        _dispatching = true;
                    }

                    Dispatch(work, cohort, settings);
                }
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            DisposeTimer();
            lock (_lock)
            {
                ClearPendingUnsafe();
                CompleteDrainUnsafe();
            }
        }
    }

    private TimeSpan GetDelayUnsafe()
    {
        var cohort = _cohort!;
        if (_handoff)
        {
            return TimeSpan.Zero;
        }

        var retryDelay = _retryTimestamp is { } retry
            ? _retryPeriod - _timeProvider.GetElapsedTime(retry) : TimeSpan.Zero;
        var complete = cohort.Membership.Members.Length > 0
            && cohort.Contributors.Count == cohort.Membership.Members.Length;
        var collectionDelay = _stopping || _drainCompletion is not null || complete
            ? TimeSpan.Zero : cohort.Period - _timeProvider.GetElapsedTime(cohort.Started);
        return collectionDelay > retryDelay ? collectionDelay : retryDelay;
    }

    private void Dispatch(PendingDispatch[] work, Cohort cohort, in Settings settings)
    {
        var notifications = new KeyNotification[Math.Min(work.Length, settings.MaxBatchItems)];
        for (var offset = 0; offset < work.Length; offset += notifications.Length)
        {
            // Revalidate before every chunk: a callback can change membership or exhaust Stop's budget.
            var membership = _getMembership();
            var count = Math.Min(notifications.Length, work.Length - offset);
            var sendCount = 0;
            lock (_lock)
            {
                membership = RefreshMembershipUnsafe(membership);
                if (_aborted || _failure is not null)
                {
                    break;
                }

                for (var i = offset; i < offset + count; i++)
                {
                    var item = work[i];
                    if (IsCurrentUnsafe(item.Pending) && IsEligible(item.Notification, membership))
                    {
                        notifications[sendCount++] = item.Notification;
                    }
                }
            }

            var accepted = false;
            try
            {
                accepted = sendCount > 0 && _dispatch(notifications.AsSpan(0, sendCount));
            }
            catch (Exception exception)
            {
                try
                {
                    LogDispatchFailed(_logger, exception, _namespaceName);
                }
                catch (Exception loggingFailure)
                {
                    Fail(new AggregateException("The root dispatch recovery diagnostic failed.", exception, loggingFailure));
                }
            }

            lock (_lock)
            {
                for (var i = offset; i < offset + count; i++)
                {
                    var item = work[i];
                    var pending = item.Pending;
                    var admitted = accepted && IsEligible(item.Notification, membership)
                        && !_aborted && _failure is null && IsCurrentUnsafe(pending);
                    CompleteReceiptUnsafe(item, new(admitted, cohort.Started, cohort.Period));
                    pending.InFlight = null;
                    if (!IsCurrentUnsafe(pending))
                    {
                        continue;
                    }

                    if (admitted && pending.Generation == item.Generation)
                    {
                        _pending.Remove(pending.Notification.Key);
                    }
                    else if (!admitted && !_aborted && _failure is null)
                    {
                        // Receipts are final even after partial child admission. Only bounded hints
                        // retry; publishers can use direct fallback without waiting on a congested child.
                        if (pending.Notification.Version == item.Notification.Version)
                        {
                            pending.Notification = pending.Notification with
                            {
                                Force = pending.Notification.Force || item.Notification.Force,
                            };
                        }

                        _cohort ??= new(membership, _timeProvider.GetTimestamp(), settings.Period);
                        if (pending.Node.List is null)
                        {
                            _ready.AddLast(pending.Node);
                        }

                        _retryTimestamp = _timeProvider.GetTimestamp();
                        _retryPeriod = settings.Period;
                    }
                }
            }
        }

        lock (_lock)
        {
            _dispatching = false;
            if (_pending.Count == 0)
            {
                CompleteDrainUnsafe();
            }
        }
    }

    private void CompleteReceiptUnsafe(PendingDispatch item, ReceiptOutcome outcome)
    {
        if (item.Receipt is null)
        {
            return;
        }

        if (item.Producer is { } producer)
        {
            if (item.ProducerVersion >= producer.CompletedVersion)
            {
                producer.CompletedVersion = item.ProducerVersion;
                producer.Outcome = outcome;
            }

            if (ReferenceEquals(producer.InFlightReceipt, item.Receipt))
            {
                producer.InFlightReceipt = null;
            }
        }

        item.Receipt.TrySetResult(outcome);
    }

    private DisseminationMembershipSnapshot RefreshMembershipUnsafe(DisseminationMembershipSnapshot membership)
    {
        if (_membership is { } current
            && (ReferenceEquals(current, membership) || membership.MembershipVersion.Value < current.MembershipVersion.Value))
        {
            return current;
        }

        _membership = membership;
        foreach (var silo in _producers.Keys.Where(silo => !membership.ContainsMember(silo)).ToArray())
        {
            _producers.Remove(silo);
        }

        foreach (var pending in _pending.Values.Where(pending => !IsEligible(pending.Notification, membership)).ToArray())
        {
            RemoveUnsafe(pending);
        }

        return membership;
    }

    private static bool IsEligible(KeyNotification notification, DisseminationMembershipSnapshot membership) =>
        notification.Key.Value is not SiloAddress silo || membership.ContainsMember(silo);

    private bool IsCurrentUnsafe(PendingKey pending) =>
        _pending.TryGetValue(pending.Notification.Key, out var current) && ReferenceEquals(current, pending);

    private void RemoveUnsafe(PendingKey pending)
    {
        var outcome = new ReceiptOutcome(false, _cohort?.Started ?? _timeProvider.GetTimestamp(), _cohort?.Period ?? TimeSpan.Zero);
        CompleteReceiptUnsafe(new(pending, pending.Generation, pending.Notification,
            pending.Receipt, pending.Producer, pending.Producer?.Version ?? 0), outcome);
        pending.InFlight?.TrySetResult(outcome);
        if (pending.Receipt is not null && pending.Notification.Key.Value is SiloAddress silo)
        {
            _cohort?.Contributors.Remove(silo);
        }

        _pending.Remove(pending.Notification.Key);
        if (pending.Node.List is not null)
        {
            _ready.Remove(pending.Node);
        }

        if (_ready.Count == 0)
        {
            _cohort = null;
        }
    }

    private Settings GetSettings()
    {
        var options = _options.CurrentValue;
        var namespaceOptions = _namespace.Options;
        var period = _namespace.AggregationPeriod;
        return new(
            options.Enabled && namespaceOptions.Enabled,
            Math.Max(1, namespaceOptions.MaxPendingItemCount),
            Math.Max(1, options.MaxBatchItems),
            TimeSpan.FromMilliseconds(Math.Clamp(period.TotalMilliseconds, 1, uint.MaxValue - 1)));
    }

    private void ClearPendingUnsafe()
    {
        foreach (var pending in _pending.Values.ToArray())
        {
            RemoveUnsafe(pending);
        }

        _cohort = null;
        _retryTimestamp = null;
        _handoff = false;
    }

    private void CompleteDrainUnsafe()
    {
        _retryTimestamp = null;
        _handoff = false;
        _drainCompletion?.TrySetResult();
        _drainCompletion = null;
    }

    private void ThrowIfFailedUnsafe()
    {
        if (_failure is { } failure)
        {
            throw new InvalidOperationException($"The dissemination root batcher for {_namespaceName} has failed.", failure);
        }
    }

    private void ThrowIfAbortedUnsafe()
    {
        if (_aborted)
        {
            throw new OperationCanceledException("The dissemination root drain was canceled.", _abortToken);
        }
    }

    private void Wake()
    {
        lock (_lock)
        {
            if (_workerActive || _failure is not null || _aborted)
            {
                return;
            }

            _workerActive = true;
        }

        try
        {
            _timer.Wake();
        }
        catch (Exception exception)
        {
            Fail(exception);
            DisposeTimer();
        }
    }

    private void DisposeTimer()
    {
        try
        {
            _timer.Dispose();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void ReportRejection(int count, int limit)
    {
        try
        {
            LogAdmissionRejected(_logger, _namespaceName, count, limit);
            for (var i = 0; i < count; i++)
            {
                try
                {
                    DisseminationInstruments.OnQueueAdmissionRejected(_namespaceName);
                }
                catch (Exception exception)
                {
                    LogDiagnosticFailed(_logger, exception, _namespaceName);
                }
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
            DisposeTimer();
        }
    }

    private void Fail(Exception exception)
    {
        lock (_lock)
        {
            if (_failure is not null)
            {
                return;
            }

            _failure = exception;
            // Signals complete normally: a canceled caller must not leave an unobserved task fault.
            ClearPendingUnsafe();
            _drainCompletion?.TrySetResult();
        }

        try
        {
            LogWorkerFailed(_logger, exception, _namespaceName);
        }
        catch
        {
            // Preserve the original failure for admission and drain callers even if logging fails.
        }
    }

    private sealed class PendingKey
    {
        public KeyNotification Notification;
        public long Generation = 1;
        public long AdmissionGeneration = 1;
        public Producer? Producer;
        public TaskCompletionSource<ReceiptOutcome>? Receipt;
        public TaskCompletionSource<ReceiptOutcome>? InFlight;
        public LinkedListNode<PendingKey> Node { get; }

        public PendingKey(KeyNotification notification)
        {
            Notification = notification;
            Node = new(this);
        }
    }

    private sealed class Producer
    {
        public long Version;
        public long CompletedVersion;
        public long InFlightVersion;
        public ReceiptOutcome Outcome;
        public TaskCompletionSource<ReceiptOutcome>? InFlightReceipt;
    }

    private sealed class Cohort(DisseminationMembershipSnapshot membership, long started, TimeSpan period)
    {
        public DisseminationMembershipSnapshot Membership { get; } = membership;
        public long Started { get; } = started;
        public TimeSpan Period { get; } = period;
        public HashSet<SiloAddress> Contributors { get; } = [];
    }

    private readonly record struct ReceiptOutcome(bool Accepted, long Started, TimeSpan Period);
    private readonly record struct PendingDispatch(
        PendingKey Pending, long Generation, KeyNotification Notification,
        TaskCompletionSource<ReceiptOutcome>? Receipt, Producer? Producer, long ProducerVersion);
    private readonly record struct Settings(bool Enabled, int MaxPendingItemCount, int MaxBatchItems, TimeSpan Period);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dissemination root dispatch for {Namespace} failed. Receipts are rejected and retained hints will retry after the publication period.")]
    private static partial void LogDispatchFailed(ILogger logger, Exception exception, DisseminationNamespace @namespace);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dissemination root for {Namespace} rejected {Count} new keys at its pending-key limit of {Limit}.")]
    private static partial void LogAdmissionRejected(ILogger logger, DisseminationNamespace @namespace, int count, int limit);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dissemination root diagnostic for {Namespace} failed.")]
    private static partial void LogDiagnosticFailed(ILogger logger, Exception exception, DisseminationNamespace @namespace);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dissemination root batcher for {Namespace} failed. Admission and drain callers will observe this failure.")]
    private static partial void LogWorkerFailed(ILogger logger, Exception exception, DisseminationNamespace @namespace);
}
