using System.Diagnostics;

namespace Orleans.Reminders.Concurrency;

/// <summary>
/// The <see cref="ActivitySource"/> used by the reminder runtime for distributed tracing.
/// </summary>
public static class RemindersActivitySource
{
    /// <summary>
    /// The name of the reminder runtime <see cref="ActivitySource"/>:
    /// <c>Microsoft.Orleans.Reminders</c>. Subscribe with an
    /// <see cref="ActivityListener"/> or OpenTelemetry trace provider to receive spans.
    /// </summary>
    public const string Name = "Microsoft.Orleans.Reminders";

    internal static readonly ActivitySource Source = new(Name, "1.0.0");
}

/// <summary>
/// Well-known attribute keys for activities and metric tags emitted by the reminder
/// runtime. Follow OpenTelemetry semantic-convention style (dotted, lowercase,
/// snake_case segments; no unit suffixes).
/// </summary>
public static class ReminderActivityAttributes
{
    /// <summary>The grain identity associated with the reminder.</summary>
    public const string GrainId = "orleans.grain.id";

    /// <summary>The grain type associated with the reminder.</summary>
    public const string GrainType = "orleans.grain.type";

    /// <summary>The reminder name.</summary>
    public const string ReminderName = "orleans.reminder.name";

    /// <summary>The tardiness of the reminder tick relative to its scheduled time, in seconds.</summary>
    public const string Tardiness = "orleans.reminder.tardiness";

    /// <summary>The tier that produced an admit or skip outcome.</summary>
    public const string ThrottleTier = "orleans.reminder.throttle.tier";

    /// <summary>The admit / skip outcome of the throttle acquire.</summary>
    public const string ThrottleOutcome = "orleans.reminder.throttle.outcome";

    /// <summary>The classified skip reason. Only set when the outcome is skipped.</summary>
    public const string ThrottleSkipReason = "orleans.reminder.throttle.skip_reason";

    /// <summary>The scope-key for per-grain-interface and per-reminder limiters.</summary>
    public const string ThrottleScopeKey = "orleans.reminder.throttle.scope_key";

    /// <summary>The failure mode of a cluster-wide limiter (<c>open</c> / <c>closed</c>).</summary>
    public const string ThrottleFailureMode = "orleans.reminder.throttle.failure_mode";
}
