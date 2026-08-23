using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime.Messaging;

namespace Orleans.Reminders.Concurrency;

internal interface IReminderDeliveryThrottleLifecycle
{
    void Start();

    void Stop();
}

/// <summary>
/// An in-process <see cref="IReminderDeliveryThrottle"/> implementation that bounds reminder
/// dispatch through a composed pipeline of admission gates.
/// </summary>
/// <remarks>
/// <para>This is the implementation that backs the Per-Silo tier.</para>
/// <para>Configured gates run in order: overload, slow-start, local concurrency, and local rate.
/// Earlier gates run first so that broad protection (for example, overload) is honored before
/// local permits or tokens are consumed.</para>
/// <para>Any <see cref="ThrottleBlockMode.WaitUpTo"/> used by the composed gates contributes to a
/// single shared end-to-end deadline measured from the start of the acquire. The shortest configured
/// timeout bounds every gate and the final admission commit.</para>
/// <para>Capacity acquired from individual gates remains a reversible reservation until every gate
/// admits and the shared deadline and cancellation token are checked. Cancellation, timeout, or a
/// later gate rejection rolls back all reservations, including rate tokens.</para>
/// </remarks>
public sealed class LocalReminderDeliveryThrottle : IReminderDeliveryThrottle, IReminderDeliveryThrottleLifecycle, IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly string _tierName;
    private readonly IReminderAdmissionGate[] _gates;
    private readonly LocalConcurrencyReminderAdmissionGate? _concurrencyGate;
    private readonly LocalRateReminderAdmissionGate? _rateGate;
    private readonly SlowStartReminderAdmissionGate? _slowStartGate;
    private readonly TimeSpan? _acquireTimeout;

    /// <summary>
    /// Initializes a new instance with the supplied configuration. Used by tests; production
    /// code should resolve the throttle through DI.
    /// </summary>
    /// <param name="config">The throttle configuration.</param>
    /// <param name="timeProvider">The time provider used for waits, rate calculations, and slow-start ramp.</param>
    /// <param name="tierName">A name for this tier reported in observability output.</param>
    /// <param name="overloadDetector">Optional silo overload detector. Required when <see cref="ThrottleConfig.Overload"/> is configured.</param>
    public LocalReminderDeliveryThrottle(ThrottleConfig config, TimeProvider timeProvider, string tierName, IOverloadDetector? overloadDetector = null)
        : this(config, timeProvider, tierName, overloadDetector, startImmediately: true)
    {
    }

    internal LocalReminderDeliveryThrottle(
        ThrottleConfig config,
        TimeProvider timeProvider,
        string tierName,
        IOverloadDetector? overloadDetector,
        bool startImmediately)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrEmpty(tierName);

        if (config.Overload is not null && overloadDetector is null)
        {
            throw new ArgumentException(
                "ThrottleConfig.Overload is configured but no IOverloadDetector was supplied. " +
                "Register IOverloadDetector in the silo's service collection or remove the RespectOverload configuration.",
                nameof(overloadDetector));
        }

        _timeProvider = timeProvider;
        _tierName = tierName;

        var gates = new List<IReminderAdmissionGate>(capacity: 4);
        if (config.Overload is { } overload)
        {
            gates.Add(new OverloadReminderAdmissionGate(overload, timeProvider, overloadDetector!));
        }

        if (config.SlowStart is { } slowStart)
        {
            _slowStartGate = new SlowStartReminderAdmissionGate(slowStart, config.Concurrency!.MaxConcurrent, timeProvider);
            gates.Add(_slowStartGate);
        }

        if (config.Concurrency is { } concurrency)
        {
            _concurrencyGate = new LocalConcurrencyReminderAdmissionGate(concurrency, timeProvider);
            gates.Add(_concurrencyGate);
        }

        if (config.Rate is { } rate)
        {
            _rateGate = new LocalRateReminderAdmissionGate(rate, timeProvider);
            gates.Add(_rateGate);
        }

        _gates = gates.ToArray();
        _acquireTimeout = GetAcquireTimeout(_gates);
        if (startImmediately)
        {
            Start();
        }
    }

    /// <summary>The tier name reported on leases produced by this throttle.</summary>
    public string TierName => _tierName;

    /// <summary>The number of currently available concurrency permits, or <c>int.MaxValue</c> when concurrency is unbounded.</summary>
    public int AvailableConcurrencyPermits => _concurrencyGate?.AvailablePermits ?? int.MaxValue;

    internal TimeProvider TimeProvider => _timeProvider;

    /// <summary>The current available token count in the rate bucket, or <c>int.MaxValue</c> when rate is unbounded.</summary>
    public int AvailableRateTokens => _rateGate?.AvailableTokens ?? int.MaxValue;

    /// <summary>The current slow-start capacity (ramps up over time toward <c>MaxConcurrent</c>).</summary>
    public int SlowStartCurrentCapacity => _slowStartGate?.CurrentCapacity ?? int.MaxValue;

    void IReminderDeliveryThrottleLifecycle.Start() => _slowStartGate?.Start();

    void IReminderDeliveryThrottleLifecycle.Stop() => _slowStartGate?.Stop();

    internal void Start() => _slowStartGate?.Start();

    internal void Stop() => _slowStartGate?.Stop();

    /// <inheritdoc />
    public async ValueTask<ReminderDeliveryLease> AcquireAsync(ReminderDeliveryContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var budget = new ReminderAcquireBudget(_timeProvider, _acquireTimeout);
        using var transaction = new ReminderAdmissionTransaction(cancellationToken);

        foreach (var gate in _gates)
        {
            if (budget.IsTimedOut)
            {
                transaction.Rollback();
                return ReminderDeliveryLease.Skipped(_tierName, budget.Elapsed, ReminderSkipReason.AcquireTimeout);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = await gate.AcquireAsync(context, budget, cancellationToken).ConfigureAwait(false);
            if (!transaction.TryAdd(result.Reservation))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ReminderDeliveryLease.Skipped(_tierName, budget.Elapsed, ReminderSkipReason.AcquireTimeout);
            }

            if (!result.AdmittedLease)
            {
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Rollback();
                return ReminderDeliveryLease.Skipped(_tierName, budget.Elapsed, result.SkipReason);
            }

            if (budget.IsTimedOut)
            {
                transaction.Rollback();
                return ReminderDeliveryLease.Skipped(_tierName, budget.Elapsed, ReminderSkipReason.AcquireTimeout);
            }
        }

        switch (transaction.TryCommit(budget, cancellationToken, out var releaseActions))
        {
            case ReminderAdmissionCommitOutcome.Cancelled:
                cancellationToken.ThrowIfCancellationRequested();
                throw new OperationCanceledException(cancellationToken);
            case ReminderAdmissionCommitOutcome.TimedOut:
                return ReminderDeliveryLease.Skipped(_tierName, budget.Elapsed, ReminderSkipReason.AcquireTimeout);
            default:
                return ReminderDeliveryLease.Admitted(_tierName, budget.Elapsed, CreateReleaseAction(releaseActions));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        for (var i = _gates.Length - 1; i >= 0; i--)
        {
            _gates[i].Dispose();
        }
    }

    private static Action? CreateReleaseAction(List<Action>? releaseActions)
    {
        if (releaseActions is null or { Count: 0 })
        {
            return null;
        }

        return () => ReleaseAcquired(releaseActions);
    }

    private static void ReleaseAcquired(List<Action>? releaseActions)
    {
        if (releaseActions is null)
        {
            return;
        }

        for (var i = releaseActions.Count - 1; i >= 0; i--)
        {
            releaseActions[i]();
        }
    }

    private static TimeSpan? GetAcquireTimeout(IReminderAdmissionGate[] gates)
    {
        TimeSpan? result = null;
        foreach (var gate in gates)
        {
            if (gate.BlockMode is ThrottleBlockMode.WaitWithTimeout waitWithTimeout
                && (result is null || waitWithTimeout.Timeout < result))
            {
                result = waitWithTimeout.Timeout;
            }
        }

        return result;
    }
}
