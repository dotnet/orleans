namespace Orleans.Runtime.ClusterServices;

internal enum TransitionGateStatus
{
    Pending,
    Completed,
    Failed,
    Aborted
}

internal enum AcquisitionPhase
{
    AwaitingState,
    StateInstalled,
    Fenced
}

internal enum ReleasePhase
{
    Blocking,
    Drained,
    StateRetained
}

/// <summary>
/// A view-scoped admission gate. Failed gates remain blocking until terminal shutdown.
/// </summary>
internal abstract class TransitionGate<TViewId>
    where TViewId : IComparable<TViewId>, IEquatable<TViewId>
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _status;

    protected TransitionGate(TViewId targetView)
    {
        ArgumentNullException.ThrowIfNull(targetView);
        TargetView = targetView;
    }

    protected object SyncRoot { get; } = new();

    public TViewId TargetView { get; }

    public Task Completion => _completion.Task;

    public TransitionGateStatus Status => (TransitionGateStatus)Volatile.Read(ref _status);

    public bool IsBlocking => Status is TransitionGateStatus.Pending or TransitionGateStatus.Failed;

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (SyncRoot)
        {
            EnsurePending();
            Volatile.Write(ref _status, (int)TransitionGateStatus.Failed);
            _completion.TrySetException(exception);
        }
    }

    public void Abort(CancellationToken cancellationToken = default)
    {
        lock (SyncRoot)
        {
            if (Status is TransitionGateStatus.Completed or TransitionGateStatus.Aborted)
            {
                return;
            }

            Volatile.Write(ref _status, (int)TransitionGateStatus.Aborted);
            // A failed task keeps its original exception even after shutdown releases admission.
            _completion.TrySetCanceled(cancellationToken.IsCancellationRequested
                ? cancellationToken
                : new CancellationToken(canceled: true));
        }
    }

    protected void EnsurePending()
    {
        if (Status is not TransitionGateStatus.Pending)
        {
            throw new InvalidOperationException($"A transition with status '{Status}' cannot progress.");
        }
    }

    protected void CompleteCore()
    {
        lock (SyncRoot)
        {
            EnsurePending();
            ValidateCompletion();
            Volatile.Write(ref _status, (int)TransitionGateStatus.Completed);
            _completion.TrySetResult();
        }
    }

    protected abstract void ValidateCompletion();
}

internal class OwnershipAcquisition<TViewId> : TransitionGate<TViewId>
    where TViewId : IComparable<TViewId>, IEquatable<TViewId>
{
    private AcquisitionPhase _phase;
    private ClusterServiceFence? _fence;

    public OwnershipAcquisition(TViewId previousView, TViewId targetView) : base(targetView)
    {
        ArgumentNullException.ThrowIfNull(previousView);
        if (targetView.CompareTo(previousView) <= 0)
        {
            throw new ArgumentException("The target view must be newer than the previous view.", nameof(targetView));
        }

        PreviousView = previousView;
    }

    public TViewId PreviousView { get; }

    public AcquisitionPhase Phase
    {
        get
        {
            lock (SyncRoot)
            {
                return _phase;
            }
        }
    }

    public ClusterServiceFence? Fence
    {
        get
        {
            lock (SyncRoot)
            {
                return _fence;
            }
        }
    }

    public void MarkStateInstalled()
    {
        lock (SyncRoot)
        {
            EnsurePending();
            if (_phase is not AcquisitionPhase.AwaitingState)
            {
                throw new InvalidOperationException("An acquisition can install state only while awaiting state.");
            }

            _phase = AcquisitionPhase.StateInstalled;
        }
    }

    public void MarkFenced(ClusterServiceFence fence)
    {
        lock (SyncRoot)
        {
            EnsurePending();
            if (_phase is not AcquisitionPhase.StateInstalled)
            {
                throw new InvalidOperationException("An acquisition must install state before establishing fencing.");
            }

            _fence = fence;
            _phase = AcquisitionPhase.Fenced;
        }
    }

    public void Complete() => CompleteCore();

    protected sealed override void ValidateCompletion()
    {
        if (_phase is not AcquisitionPhase.Fenced)
        {
            throw new InvalidOperationException("An acquisition must install state and establish fencing before completion.");
        }
    }
}

internal class OwnershipRelease<TViewId> : TransitionGate<TViewId>
    where TViewId : IComparable<TViewId>, IEquatable<TViewId>
{
    private ReleasePhase _phase;

    public OwnershipRelease(TViewId previousView, TViewId targetView) : base(targetView)
    {
        ArgumentNullException.ThrowIfNull(previousView);
        if (targetView.CompareTo(previousView) <= 0)
        {
            throw new ArgumentException("The target view must be newer than the previous view.", nameof(targetView));
        }

        PreviousView = previousView;
    }

    public TViewId PreviousView { get; }

    public ReleasePhase Phase
    {
        get
        {
            lock (SyncRoot)
            {
                return _phase;
            }
        }
    }

    public void MarkDrained()
    {
        lock (SyncRoot)
        {
            EnsurePending();
            if (_phase is not ReleasePhase.Blocking)
            {
                throw new InvalidOperationException("A release can drain only while blocking.");
            }

            _phase = ReleasePhase.Drained;
        }
    }

    public void MarkStateRetained()
    {
        lock (SyncRoot)
        {
            EnsurePending();
            if (_phase is not ReleasePhase.Drained)
            {
                throw new InvalidOperationException("A release must drain before retaining state.");
            }

            _phase = ReleasePhase.StateRetained;
        }
    }

    public void Complete() => CompleteCore();

    protected sealed override void ValidateCompletion()
    {
        if (_phase is not (ReleasePhase.Drained or ReleasePhase.StateRetained))
        {
            throw new InvalidOperationException("A release must drain operations before completion.");
        }
    }
}

internal class ViewBarrier<TViewId>(TViewId targetView) : TransitionGate<TViewId>(targetView)
    where TViewId : IComparable<TViewId>, IEquatable<TViewId>
{
    public void Complete() => CompleteCore();

    protected sealed override void ValidateCompletion()
    {
    }
}
