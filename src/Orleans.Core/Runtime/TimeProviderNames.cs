namespace Orleans.Runtime;

/// <summary>
/// Service keys used to resolve per-area <see cref="System.TimeProvider"/> instances from dependency injection for
/// the areas owned by the Orleans core runtime. Individual extension packages (reminders, streaming, transactions,
/// durable jobs, journaling, and similar) define their own keys in the corresponding package.
/// </summary>
/// <remarks>
/// <para>
/// Orleans resolves time from a single default <see cref="System.TimeProvider"/> registration. To allow individual
/// subsystems to be driven by different clocks, each area also resolves a <em>keyed</em> <see cref="System.TimeProvider"/>
/// using one of these keys. A catch-all <see cref="Microsoft.Extensions.DependencyInjection.KeyedService.AnyKey"/>
/// registration resolves every key to the unkeyed default provider, so behavior is unchanged unless a specific area
/// is overridden.
/// </para>
/// <para>
/// This is primarily useful for testing: a test can install a controllable clock (such as
/// <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c>) as the default provider to drive grain timers and
/// grain-facing delays deterministically, while pinning the silo''s background maintenance timers to real time so that
/// advancing the fake clock does not resume those loops inline.
/// </para>
/// </remarks>
public static class TimeProviderNames
{
    /// <summary>
    /// The clock exposed to grains via <c>IGrainRuntime.TimeProvider</c> and used to drive grain timers.
    /// </summary>
    public const string Grains = "Orleans.Grains";

    /// <summary>
    /// The clock used by messaging infrastructure, such as request timeout tracking and gateway maintenance.
    /// </summary>
    public const string Messaging = "Orleans.Messaging";

    /// <summary>
    /// The clock used by silo background maintenance loops driven by <c>IAsyncTimerFactory</c>
    /// (grain directory maintenance and similar), excluding cluster membership, which uses
    /// <see cref="Membership"/>.
    /// </summary>
    public const string SystemTimers = "Orleans.SystemTimers";

    /// <summary>
    /// The clock used by cluster membership loops, including the membership agent, membership table
    /// maintenance and cleanup, and silo health monitoring and probing.
    /// </summary>
    public const string Membership = "Orleans.Membership";

    /// <summary>
    /// The clock used by activation lifecycle management, including activation collection, migratability checks,
    /// repartitioning, and rebalancing.
    /// </summary>
    public const string ActivationManagement = "Orleans.ActivationManagement";

    /// <summary>
    /// The clock used by the grain directory cache for entry expiration.
    /// </summary>
    public const string GrainDirectory = "Orleans.GrainDirectory";
}
