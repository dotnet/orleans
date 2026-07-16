using System.Collections.Generic;

namespace Orleans.Runtime;

/// <summary>
/// Service keys used to resolve per-area <see cref="System.TimeProvider"/> instances from dependency injection.
/// </summary>
/// <remarks>
/// <para>
/// Orleans resolves time from a single default <see cref="System.TimeProvider"/> registration. To allow individual
/// subsystems to be driven by different clocks, each area also resolves a <em>keyed</em> <see cref="System.TimeProvider"/>
/// using one of the keys defined here. Every keyed provider defaults to the unkeyed default provider, so behavior is
/// unchanged unless a specific area is overridden.
/// </para>
/// <para>
/// This is primarily useful for testing: a test can install a controllable clock (such as
/// <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c>) as the default provider to drive grain timers and
/// grain-facing delays deterministically, while pinning the silo's background maintenance timers (see
/// <see cref="BackgroundAreas"/>) to real time so that advancing the fake clock does not resume those loops inline.
/// </para>
/// </remarks>
public static class TimeProviderNames
{
    /// <summary>
    /// The clock exposed to grains via <c>IGrainRuntime.TimeProvider</c> and used to drive grain timers.
    /// </summary>
    public const string Grains = "Orleans.TimeProvider.Grains";

    /// <summary>
    /// The clock used by the reminder subsystem.
    /// </summary>
    public const string Reminders = "Orleans.TimeProvider.Reminders";

    /// <summary>
    /// The clock used by messaging infrastructure, such as request timeout tracking and gateway maintenance.
    /// </summary>
    public const string Messaging = "Orleans.TimeProvider.Messaging";

    /// <summary>
    /// The clock used by silo background maintenance loops driven by <c>IAsyncTimerFactory</c>
    /// (cluster membership, grain directory maintenance, health checks, and similar).
    /// </summary>
    public const string SystemTimers = "Orleans.TimeProvider.SystemTimers";

    /// <summary>
    /// The clock used by activation lifecycle management, including activation collection, migratability checks,
    /// repartitioning, and rebalancing.
    /// </summary>
    public const string ActivationManagement = "Orleans.TimeProvider.ActivationManagement";

    /// <summary>
    /// The clock used by the grain directory cache for entry expiration.
    /// </summary>
    public const string GrainDirectory = "Orleans.TimeProvider.GrainDirectory";

    /// <summary>
    /// The clock used by the streaming subsystem (pulling agents and managers, queue balancers).
    /// </summary>
    public const string Streaming = "Orleans.TimeProvider.Streaming";

    /// <summary>
    /// The clock used by the transactions subsystem.
    /// </summary>
    public const string Transactions = "Orleans.TimeProvider.Transactions";

    /// <summary>
    /// The clock used by the durable jobs subsystem.
    /// </summary>
    public const string DurableJobs = "Orleans.TimeProvider.DurableJobs";

    /// <summary>
    /// The clock used by the journaling subsystem.
    /// </summary>
    public const string Journaling = "Orleans.TimeProvider.Journaling";

    /// <summary>
    /// The clock used by internal caches with time-based expiration.
    /// </summary>
    public const string Caching = "Orleans.TimeProvider.Caching";

    /// <summary>
    /// Gets the set of area keys which drive background/infrastructure timers. These areas should run on real time
    /// even when the default provider is a controllable test clock, otherwise advancing that clock can resume the
    /// corresponding background loops inline. Excludes grain-facing areas such as <see cref="Grains"/>.
    /// </summary>
    public static IReadOnlyList<string> BackgroundAreas { get; } =
    [
        Reminders,
        Messaging,
        SystemTimers,
        ActivationManagement,
        GrainDirectory,
        Streaming,
        Transactions,
        DurableJobs,
        Journaling,
        Caching,
    ];
}
