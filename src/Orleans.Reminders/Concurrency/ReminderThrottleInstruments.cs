using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using Orleans.Runtime;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// Diagnostic instruments for reminder concurrency control. Created as a singleton when
/// reminders are added to the silo and registered with the shared <see cref="OrleansInstruments"/>
/// meter (<c>Microsoft.Orleans</c>) so all Orleans metrics flow through a single OpenTelemetry
/// scope.
/// </summary>
/// <remarks>
/// Metric names follow the existing Orleans <c>orleans-reminders-*</c> convention
/// (kebab-case). Tag keys use OpenTelemetry semantic-convention style
/// (<c>orleans.reminder.throttle.tier</c>, etc.).
/// </remarks>
public sealed class ReminderThrottleInstruments
{
    private readonly Histogram<double> _acquireDuration;
    private readonly ConcurrentDictionary<string, int> _activeLeasesByTier = new(StringComparer.Ordinal);
    private readonly ObservableUpDownCounter<int> _activeLeases;
    private readonly Counter<int> _ticksSkipped;
    private readonly Counter<int> _coordinatorOutages;

    /// <summary>Initializes the instruments using the shared Orleans meter.</summary>
    /// <param name="instruments">The shared Orleans instrument set.</param>
    public ReminderThrottleInstruments(OrleansInstruments instruments)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        _acquireDuration = instruments.Meter.CreateHistogram<double>(
            name: "orleans-reminders-throttle-queued-duration",
            unit: "s",
            description: "Time a reminder tick spent waiting for a throttle lease, before admission or skip.");

        _activeLeases = instruments.Meter.CreateObservableUpDownCounter<int>(
            name: "orleans-reminders-throttle-active-leases",
            observeValues: ObserveActiveLeases,
            unit: "{lease}",
            description: "The number of currently-held reminder throttle leases.");

        _ticksSkipped = instruments.Meter.CreateCounter<int>(
            name: "orleans-reminders-ticks-skipped",
            unit: "{tick}",
            description: "The number of reminder ticks skipped by a throttle.");

        _coordinatorOutages = instruments.Meter.CreateCounter<int>(
            name: "orleans-reminders-throttle-coordinator-outages",
            unit: "{event}",
            description: "Cluster-wide throttle coordinator outage transitions.");
    }

    /// <summary>Records the duration of a throttle acquire (admit or skip).</summary>
    public void RecordAcquireDuration(string? tier, ReminderAdmissionOutcome outcome, TimeSpan duration)
    {
        if (!_acquireDuration.Enabled)
        {
            return;
        }

        _acquireDuration.Record(
            duration.TotalSeconds,
            new KeyValuePair<string, object?>(ReminderActivityAttributes.ThrottleTier, tier ?? "(none)"),
            new KeyValuePair<string, object?>(ReminderActivityAttributes.ThrottleOutcome, FormatOutcome(outcome)));
    }

    /// <summary>Increments the active lease count for an admitted lease.</summary>
    public void OnLeaseAcquired(string? tier)
    {
        _activeLeasesByTier.AddOrUpdate(tier ?? "(none)", 1, static (_, count) => count + 1);
    }

    /// <summary>Decrements the active lease count for a released lease.</summary>
    public void OnLeaseReleased(string? tier)
    {
        _activeLeasesByTier.AddOrUpdate(tier ?? "(none)", -1, static (_, count) => count - 1);
    }

    private IEnumerable<Measurement<int>> ObserveActiveLeases()
    {
        foreach (var (tier, count) in _activeLeasesByTier)
        {
            yield return new Measurement<int>(
                count,
                new KeyValuePair<string, object?>(ReminderActivityAttributes.ThrottleTier, tier));
        }
    }

    /// <summary>Records a tick being skipped by a throttle.</summary>
    public void OnTickSkipped(string? tier, ReminderSkipReason reason)
    {
        if (!_ticksSkipped.Enabled)
        {
            return;
        }

        _ticksSkipped.Add(
            1,
            new KeyValuePair<string, object?>(ReminderActivityAttributes.ThrottleTier, tier ?? "(none)"),
            new KeyValuePair<string, object?>(ReminderActivityAttributes.ThrottleSkipReason, FormatSkipReason(reason)));
    }

    /// <summary>Records a coordinator outage transition for cluster-wide tiers.</summary>
    public void OnCoordinatorOutage(string tier, ThrottleFailureMode failureMode)
    {
        if (!_coordinatorOutages.Enabled)
        {
            return;
        }

        _coordinatorOutages.Add(
            1,
            new KeyValuePair<string, object?>(ReminderActivityAttributes.ThrottleTier, tier),
            new KeyValuePair<string, object?>(ReminderActivityAttributes.ThrottleFailureMode, failureMode == Concurrency.ThrottleFailureMode.Open ? "open" : "closed"));
    }

    internal static string FormatOutcome(ReminderAdmissionOutcome outcome) => outcome switch
    {
        ReminderAdmissionOutcome.Admitted => "admitted",
        ReminderAdmissionOutcome.Skipped => "skipped",
        _ => "unknown",
    };

    internal static string FormatSkipReason(ReminderSkipReason reason) => reason switch
    {
        ReminderSkipReason.LocalLimiterFull => "local_limiter_full",
        ReminderSkipReason.ClusterLimiterFull => "cluster_limiter_full",
        ReminderSkipReason.AcquireTimeout => "acquire_timeout",
        ReminderSkipReason.CoordinatorUnreachableFailClosed => "coordinator_unreachable_fail_closed",
        ReminderSkipReason.SiloOverloaded => "silo_overloaded",
        ReminderSkipReason.SlowStartLimited => "slow_start_limited",
        ReminderSkipReason.SiloShutdown => "silo_shutdown",
        _ => "unknown",
    };
}
