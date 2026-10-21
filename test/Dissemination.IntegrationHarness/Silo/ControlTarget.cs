using Orleans.Runtime;
using Orleans.Runtime.MembershipService;

namespace Orleans.Dissemination.IntegrationHarness;

[Alias("dissemination-evidence-control-v1")]
internal interface IControlTarget : ISystemTarget
{
    [Alias("echo-v1")]
    Task<int> Echo(CancellationToken cancellationToken);

    [Alias("hold-v1")]
    Task Hold(CancellationToken cancellationToken);
}

internal sealed class ControlTarget : SystemTarget, IControlTarget
{
    public static readonly GrainType TargetType = SystemTargetGrainId.CreateGrainType("test.dissemination-evidence");
    private int _pending;
    private int _started;
    private int _cancelled;
    private int _cancellationSignals;
    private int _canBeCanceled;
    private int _cancelledOnEntry;
    private long _startedAtTicks;
    private long _cancellationObservedAtTicks;

    public ControlTarget(SystemTargetShared shared) : base(TargetType, shared) =>
        shared.ActivationDirectory.RecordNewTarget(this);

    public int Pending => Volatile.Read(ref _pending);
    public int Started => Volatile.Read(ref _started);
    public int Cancelled => Volatile.Read(ref _cancelled);
    public int CancellationSignals => Volatile.Read(ref _cancellationSignals);
    public bool TokenCanBeCanceled => Volatile.Read(ref _canBeCanceled) != 0;
    public bool TokenCancelledOnEntry => Volatile.Read(ref _cancelledOnEntry) != 0;
    public DateTimeOffset? StartedAtUtc => Timestamp(Interlocked.Read(ref _startedAtTicks));
    public DateTimeOffset? CancellationObservedAtUtc => Timestamp(Interlocked.Read(ref _cancellationObservedAtTicks));

    public Task<int> Echo(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Environment.ProcessId);
    }

    public async Task Hold(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _pending);
        Interlocked.Increment(ref _started);
        Volatile.Write(ref _canBeCanceled, cancellationToken.CanBeCanceled ? 1 : 0);
        Volatile.Write(ref _cancelledOnEntry, cancellationToken.IsCancellationRequested ? 1 : 0);
        Interlocked.Exchange(ref _startedAtTicks, DateTime.UtcNow.Ticks);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _cancellationObservedAtTicks, DateTime.UtcNow.Ticks);
            Interlocked.Increment(ref _cancellationSignals);
            throw;
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _cancelled);
            }

            Interlocked.Decrement(ref _pending);
        }
    }

    private static DateTimeOffset? Timestamp(long ticks) =>
        ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
}

internal sealed class GatedMembershipGossiper(MembershipGossiper inner) : IMembershipGossiper
{
    private int _suppressed;

    public bool Suppressed
    {
        get => Volatile.Read(ref _suppressed) != 0;
        set => Volatile.Write(ref _suppressed, value ? 1 : 0);
    }

    public Task GossipToRemoteSilos(
        List<SiloAddress> gossipPartners,
        MembershipTableSnapshot snapshot,
        SiloAddress updatedSilo,
#if NEW_RUNTIME
        SiloStatus updatedStatus,
        CancellationToken cancellationToken)
#else
        SiloStatus updatedStatus)
#endif
    {
#if NEW_RUNTIME
        cancellationToken.ThrowIfCancellationRequested();
        return Suppressed
            ? Task.CompletedTask
            : inner.GossipToRemoteSilos(gossipPartners, snapshot, updatedSilo, updatedStatus, cancellationToken);
#else
        return Suppressed
            ? Task.CompletedTask
            : inner.GossipToRemoteSilos(gossipPartners, snapshot, updatedSilo, updatedStatus);
#endif
    }
}
