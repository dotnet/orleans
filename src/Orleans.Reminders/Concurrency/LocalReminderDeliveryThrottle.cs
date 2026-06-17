using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime.Messaging;

namespace Orleans.Reminders.Concurrency;

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
/// single shared end-to-end deadline. A later gate never restarts the timeout budget after an
/// earlier gate has already spent part of it.</para>
/// </remarks>
public sealed class LocalReminderDeliveryThrottle : IReminderDeliveryThrottle, IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly string _tierName;
    private readonly IReminderAdmissionGate[] _gates;
    private readonly LocalConcurrencyReminderAdmissionGate? _concurrencyGate;
    private readonly LocalRateReminderAdmissionGate? _rateGate;
    private readonly SlowStartReminderAdmissionGate? _slowStartGate;

    /// <summary>
    /// Initializes a new instance with the supplied configuration. Used by tests; production
    /// code should resolve the throttle through DI.
    /// </summary>
    /// <param name="config">The throttle configuration.</param>
    /// <param name="timeProvider">The time provider used for waits, rate calculations, and slow-start ramp.</param>
    /// <param name="tierName">A name for this tier reported in observability output.</param>
    /// <param name="overloadDetector">Optional silo overload detector. Required when <see cref="ThrottleConfig.Overload"/> is configured.</param>
    public LocalReminderDeliveryThrottle(ThrottleConfig config, TimeProvider timeProvider, string tierName, IOverloadDetector? overloadDetector = null)
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
    }

    /// <summary>The tier name reported on leases produced by this throttle.</summary>
    public string TierName => _tierName;

    /// <summary>The number of currently available concurrency permits, or <c>int.MaxValue</c> when concurrency is unbounded.</summary>
    public int AvailableConcurrencyPermits => _concurrencyGate?.AvailablePermits ?? int.MaxValue;

    /// <summary>The current available token count in the rate bucket, or <c>int.MaxValue</c> when rate is unbounded.</summary>
    public int AvailableRateTokens => _rateGate?.AvailableTokens ?? int.MaxValue;

    /// <summary>The current slow-start capacity (ramps up over time toward <c>MaxConcurrent</c>).</summary>
    public int SlowStartCurrentCapacity => _slowStartGate?.CurrentCapacity ?? int.MaxValue;

    /// <inheritdoc />
    public async ValueTask<ReminderDeliveryLease> AcquireAsync(ReminderDeliveryContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var budget = new ReminderAcquireBudget(_timeProvider);
        List<Action>? releaseActions = null;

        foreach (var gate in _gates)
        {
            GateAcquireResult result;
            try
            {
                result = await gate.AcquireAsync(context, budget, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                ReleaseAcquired(releaseActions);
                throw;
            }

            if (!result.AdmittedLease)
            {
                ReleaseAcquired(releaseActions);
                return ReminderDeliveryLease.Skipped(_tierName, budget.Elapsed, result.SkipReason);
            }

            if (result.ReleaseAction is not null)
            {
                releaseActions ??= new List<Action>(capacity: 2);
                releaseActions.Add(result.ReleaseAction);
            }
        }

        return ReminderDeliveryLease.Admitted(_tierName, budget.Elapsed, CreateReleaseAction(releaseActions));
    }

    /// <inheritdoc />
    public void Dispose()
    {
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
}
