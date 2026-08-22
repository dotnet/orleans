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
    /// Default is <see langword="false"/>. Upsert is a blind write in every built-in Orleans reminder provider;
    /// <see cref="IReminderTable.RemoveRow"/> is the only conditional operation in the contract. Providers which
    /// implement conditional upsert may opt in, which enables the stale-ETag rejection guarantee.
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
    /// Gets or sets a value indicating whether <see cref="IReminderTable.StopAsync"/> may be invoked by the suite.
    /// </summary>
    /// <remarks>
    /// Default is <see langword="true"/>. Set to <see langword="false"/> when a shared provider instance is reused
    /// across a test class and stopping it would invalidate later tests.
    /// </remarks>
    public bool SupportsStopAsync { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the provider accepts concurrent operations against the same row.
    /// </summary>
    public bool SupportsConcurrentOperations { get; set; } = true;

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
        SupportsStopAsync = true,
        SupportsConcurrentOperations = true,
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
            SupportsStopAsync,
            nameof(ReminderTableTestRunner.ReminderTable_StopAsync_ThenRestart_ResumesService),
            nameof(SupportsStopAsync));
        AddIfDisabled(
            SupportsConditionalUpsert,
            nameof(ReminderTableTestRunner.ReminderTable_UpsertRow_WithStaleETag_IsRejected),
            nameof(SupportsConditionalUpsert));
        AddIfDisabled(
            SupportsConcurrentOperations,
            nameof(ReminderTableTestRunner.ReminderTable_ConcurrentUpserts_ProduceDistinctETags),
            nameof(SupportsConcurrentOperations));
        AddIfDisabled(
            SupportsConcurrentOperations,
            nameof(ReminderTableTestRunner.ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated),
            nameof(SupportsConcurrentOperations));
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
