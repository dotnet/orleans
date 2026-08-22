using System;
using System.Collections.Generic;

namespace Orleans.Reminders.TestKit;

internal sealed class DisabledReminderTableGuarantee
{
    public DisabledReminderTableGuarantee(string methodName, string reason)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            throw new ArgumentException("A disabled guarantee must name its runner method.", nameof(methodName));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A disabled guarantee must provide a reason.", nameof(reason));
        }

        MethodName = methodName;
        Reason = reason;
    }

    public string MethodName { get; }

    public string Reason { get; }
}

/// <summary>
/// Declares the optional parts of the <see cref="IReminderTable"/> contract which a provider under test supports.
/// </summary>
/// <remarks>
/// <para>
/// Every guarantee expressed by <see cref="ReminderTableTestRunner"/> is either mandatory for all providers or
/// gated by exactly one property on this type. Capability differences are therefore explicit and documented
/// rather than tests being silently omitted from a provider suite.
/// </para>
/// <para>
/// A capability-gated conformance test which is disabled does not silently pass: the runner records an explicit
/// skip reason which is available through <see cref="ReminderTableTestRunner.SkippedGuarantees"/>.
/// </para>
/// </remarks>
public sealed class ReminderTableCapabilities
{
    /// <summary>
    /// Gets or sets the provider name reported in every conformance failure message.
    /// </summary>
    public string ProviderName { get; set; } = "ReminderTable";

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="ReminderEntry.StartAt"/> round-trips with better than
    /// whole-second precision.
    /// </summary>
    /// <remarks>
    /// Default is <see langword="false"/>. Several backing stores (Azure Table, Cosmos DB, and some ADO.NET
    /// column types) truncate or round sub-second components, so the portable contract only guarantees
    /// whole-second UTC round-tripping. Providers which persist full <see cref="DateTime.Ticks"/> fidelity may
    /// opt in.
    /// </remarks>
    public bool SupportsSubSecondPrecision { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="IReminderTable.UpsertRow"/> validates
    /// <see cref="ReminderEntry.ETag"/> as a precondition.
    /// </summary>
    /// <remarks>
    /// Default is <see langword="false"/>, which defines the portable blind-upsert contract.
    /// Providers such as Cosmos DB and Firestore which apply the supplied ETag as a write precondition opt in,
    /// enabling the stale-ETag rejection guarantee.
    /// </remarks>
    public bool SupportsConditionalUpsert { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="IReminderTable.StartAsync"/> observes cancellation.
    /// </summary>
    /// <remarks>
    /// Default is <see langword="false"/>. The interface accepts a <see cref="System.Threading.CancellationToken"/>
    /// but does not require providers to observe it during initialization.
    /// </remarks>
    public bool SupportsStartCancellation { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a stopped table can be started again and resume serving durable rows.
    /// </summary>
    /// <remarks>
    /// Default is <see langword="false"/>. Restartability is not part of the portable contract and providers whose
    /// initialization completion source is one-shot cannot restart after <see cref="IReminderTable.StopAsync"/>.
    /// </remarks>
    public bool SupportsRestartAfterStop { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether concurrent upserts of one identity all succeed with non-empty,
    /// distinct ETags.
    /// </summary>
    public bool SupportsSameIdentityConcurrentUpserts { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether distinct reminder rows can be written in parallel without losing or
    /// mixing their identities and payloads.
    /// </summary>
    public bool SupportsParallelDistinctRows { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether every successful replacement of one row rotates its ETag.
    /// </summary>
    /// <remarks>
    /// When disabled, schedule and payload replacement are still verified. Providers which derive ETags from
    /// finite-resolution timestamps can therefore retain state conformance without claiming per-write rotation.
    /// </remarks>
    public bool SupportsETagRotation { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether hash range comparisons use the unsigned 32-bit ring ordering at the
    /// signed boundary and across ring wrap-around.
    /// </summary>
    /// <remarks>
    /// The full-ring <c>(0, 0]</c> and exact-cardinality guarantees do not depend on this capability.
    /// </remarks>
    public bool SupportsUnsignedHashRangeBoundaries { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the fixture can create a second, independently scoped table
    /// (different service or cluster identity) for cross-table isolation checks.
    /// </summary>
    /// <remarks>
    /// Default is <see langword="false"/>. Enable it by also supplying
    /// <see cref="ReminderTableTestRunner.CreateIsolatedTableAsync"/> in the derived runner.
    /// </remarks>
    public bool SupportsCrossTableIsolation { get; set; }

    /// <summary>
    /// Gets or sets the number of concurrent upserts issued against a single row by the concurrency guarantee.
    /// </summary>
    /// <remarks>Must be at least two for the guarantee to be meaningful.</remarks>
    public int ConcurrentUpsertCount { get; set; } = 5;

    /// <summary>
    /// Gets or sets the number of distinct grains used by the parallel-isolation guarantee.
    /// </summary>
    public int ParallelGrainCount { get; set; } = 5;

    /// <summary>
    /// Gets or sets the maximum number of concurrent mutations used to populate and clean up exact-cardinality tests.
    /// </summary>
    /// <remarks>
    /// Providers whose storage engine serializes reminder-table mutations can set this value to one.
    /// </remarks>
    public int CardinalityMutationBatchSize { get; set; } = 12;

    /// <summary>
    /// Gets or sets the bounded period during which reads and enumerations may converge after a mutation.
    /// </summary>
    /// <remarks>
    /// A value of <see cref="TimeSpan.Zero"/> requires the first read to observe the mutation and performs no delay.
    /// A positive value enables retries until the expected state is observed or the timeout expires.
    /// </remarks>
    public TimeSpan ReadConvergenceTimeout { get; set; }

    /// <summary>
    /// Gets or sets the delay between read-convergence attempts.
    /// </summary>
    public TimeSpan ReadConvergenceDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Creates a strict capability set in which every optional guarantee is enabled.
    /// </summary>
    /// <param name="providerName">The provider name reported in failure messages.</param>
    /// <returns>A capability set with every optional guarantee enabled.</returns>
    public static ReminderTableCapabilities Strict(string providerName) => new()
    {
        ProviderName = providerName,
        SupportsSubSecondPrecision = true,
        SupportsConditionalUpsert = true,
        SupportsStartCancellation = true,
        SupportsRestartAfterStop = true,
        SupportsSameIdentityConcurrentUpserts = true,
        SupportsParallelDistinctRows = true,
        SupportsETagRotation = true,
        SupportsUnsignedHashRangeBoundaries = true,
        SupportsCrossTableIsolation = true
    };

    /// <summary>
    /// Creates the portable capability set which every built-in Orleans reminder provider satisfies.
    /// </summary>
    /// <param name="providerName">The provider name reported in failure messages.</param>
    /// <returns>A capability set containing only the portable guarantees.</returns>
    public static ReminderTableCapabilities Portable(string providerName) => new() { ProviderName = providerName };

    internal IReadOnlyList<DisabledReminderTableGuarantee> CreateDisabledGuarantees()
    {
        var result = new List<DisabledReminderTableGuarantee>();
        var methodNames = new HashSet<string>(StringComparer.Ordinal);

        AddIfDisabled(
            SupportsRestartAfterStop,
            nameof(ReminderTableTestRunner.ReminderTable_StopAsync_ThenRestart_ResumesService),
            nameof(SupportsRestartAfterStop));
        AddIfDisabled(
            SupportsETagRotation,
            nameof(ReminderTableTestRunner.ReminderTable_UpsertRow_ReplacesETagOnEachWrite),
            nameof(SupportsETagRotation));
        AddIfDisabled(
            SupportsETagRotation,
            nameof(ReminderTableTestRunner.ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow),
            nameof(SupportsETagRotation));
        AddIfDisabled(
            SupportsConditionalUpsert,
            nameof(ReminderTableTestRunner.ReminderTable_UpsertRow_WithStaleETag_IsRejected),
            nameof(SupportsConditionalUpsert));
        AddIfDisabled(
            SupportsSameIdentityConcurrentUpserts,
            nameof(ReminderTableTestRunner.ReminderTable_ConcurrentUpserts_ProduceDistinctETags),
            nameof(SupportsSameIdentityConcurrentUpserts));
        AddIfDisabled(
            SupportsParallelDistinctRows,
            nameof(ReminderTableTestRunner.ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated),
            nameof(SupportsParallelDistinctRows));
        AddIfDisabled(
            SupportsUnsignedHashRangeBoundaries,
            nameof(ReminderTableTestRunner.ReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering),
            nameof(SupportsUnsignedHashRangeBoundaries));
        AddIfDisabled(
            SupportsUnsignedHashRangeBoundaries,
            nameof(ReminderTableTestRunner.ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd),
            nameof(SupportsUnsignedHashRangeBoundaries));
        AddIfDisabled(
            SupportsUnsignedHashRangeBoundaries,
            nameof(ReminderTableTestRunner.ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment),
            nameof(SupportsUnsignedHashRangeBoundaries));
        AddIfDisabled(
            SupportsCrossTableIsolation,
            nameof(ReminderTableTestRunner.ReminderTable_SeparatelyScopedTables_DoNotShareReminders),
            nameof(SupportsCrossTableIsolation));
        AddIfDisabled(
            SupportsStartCancellation,
            nameof(ReminderTableTestRunner.ReminderTable_StartAsync_WithCanceledToken_ThrowsOperationCanceled),
            nameof(SupportsStartCancellation));

        return result.AsReadOnly();

        void AddIfDisabled(bool supported, string methodName, string capabilityName)
        {
            if (supported)
            {
                return;
            }

            if (!methodNames.Add(methodName))
            {
                throw new InvalidOperationException($"The disabled guarantee manifest contains duplicate method '{methodName}'.");
            }

            result.Add(new DisabledReminderTableGuarantee(
                methodName,
                $"{ProviderName} does not declare {nameof(ReminderTableCapabilities)}.{capabilityName}."));
        }
    }
}
