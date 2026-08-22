using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Reminders.TestKit;

/// <summary>
/// The executable definition of the observable <see cref="IReminderTable"/> correctness contract.
/// </summary>
/// <remarks>
/// <para>
/// This runner is deliberately framework neutral: no test attributes are applied and failures are reported by
/// throwing <see cref="ReminderConformanceException"/> carrying a structured <see cref="ReminderFailureReport"/>.
/// Derive from it in a provider suite, apply your test framework's attributes to overrides, and call the base
/// implementation, exactly as <c>Orleans.Persistence.TestKit.GrainStorageTestRunner</c> is consumed.
/// </para>
/// <para>
/// Guarantees which are not universal across the built-in providers are gated by a single property of
/// <see cref="ReminderTableCapabilities"/>. A gated guarantee which is disabled records an explicit entry in
/// <see cref="SkippedGuarantees"/> instead of silently passing.
/// </para>
/// </remarks>
public abstract class ReminderTableTestRunner
{
    private readonly IReadOnlyDictionary<string, string> _skipped;
    private int _grainCounter;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderTableTestRunner"/> class.
    /// </summary>
    /// <param name="reminderTable">The reminder table under test.</param>
    /// <param name="capabilities">The capabilities declared by the provider, or <see langword="null"/> for the portable set.</param>
    /// <param name="seed">The deterministic seed used to generate reminder identities.</param>
    protected ReminderTableTestRunner(IReminderTable reminderTable, ReminderTableCapabilities? capabilities = null, int seed = 0)
    {
        ReminderTable = reminderTable ?? throw new ArgumentNullException(nameof(reminderTable));
        Capabilities = capabilities ?? ReminderTableCapabilities.Portable(reminderTable.GetType().Name);
        var disabledGuarantees = Capabilities.CreateDisabledGuarantees();
        _skipped = new ReadOnlyDictionary<string, string>(
            disabledGuarantees.ToDictionary(guarantee => guarantee.MethodName, guarantee => guarantee.Reason, StringComparer.Ordinal));
        Seed = seed;
    }

    /// <summary>
    /// Gets the reminder table under test.
    /// </summary>
    public IReminderTable ReminderTable { get; }

    /// <summary>
    /// Gets the capabilities declared by the provider under test.
    /// </summary>
    public ReminderTableCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the deterministic seed used to generate reminder identities.
    /// </summary>
    public int Seed { get; }

    /// <summary>
    /// Gets the guarantees which are disabled, keyed by guarantee name, with the capability which disabled them.
    /// </summary>
    /// <remarks>The complete, read-only manifest is created when this runner is constructed.</remarks>
    public IReadOnlyDictionary<string, string> SkippedGuarantees => _skipped;

    /// <summary>
    /// Gets the provider name reported in failure messages.
    /// </summary>
    protected string ProviderName => Capabilities.ProviderName;

    /// <summary>
    /// Gets a deterministic base time used by schedule-sensitive guarantees.
    /// </summary>
    /// <remarks>The value is truncated to whole seconds so it round-trips through every built-in provider.</remarks>
    protected virtual DateTime BaseTime { get; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Creates a second, independently scoped reminder table for cross-table isolation checks.
    /// </summary>
    /// <returns>The isolated table, or <see langword="null"/> when the provider cannot create one.</returns>
    /// <remarks>Override together with <see cref="ReminderTableCapabilities.SupportsCrossTableIsolation"/>.</remarks>
    protected virtual Task<IReminderTable?> CreateIsolatedTableAsync() => Task.FromResult<IReminderTable?>(null);

    // ---------------------------------------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Guarantee: <see cref="IReminderTable.StartAsync"/> is idempotent and leaves the table usable.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_StartAsync_IsIdempotent()
    {
        const string Guarantee = nameof(ReminderTable_StartAsync_IsIdempotent);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await ReminderTable.StartAsync(cancellation.Token);
        await ReminderTable.StartAsync(cancellation.Token);

        var grainId = NewGrainId("start-idempotent");
        var entry = NewEntry(grainId, "start-idempotent");
        var etag = await ReminderTable.UpsertRow(entry);
        if (string.IsNullOrEmpty(etag))
        {
            Report(Guarantee, "UpsertRow")
                .WithIdentity(grainId, entry.ReminderName)
                .WithExpected("a non-empty ETag after a repeated StartAsync")
                .WithObserved($"ETag={FormatETag(etag)}")
                .WithETags(etag)
                .Throw();
        }

        await RemoveAsync(grainId, entry.ReminderName, etag!);
    }

    /// <summary>
    /// Guarantee: after <see cref="IReminderTable.StopAsync"/> the table can be restarted and resumes serving reads.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_StopAsync_ThenRestart_ResumesService()
    {
        const string Guarantee = nameof(ReminderTable_StopAsync_ThenRestart_ResumesService);
        if (!Require(Guarantee, Capabilities.SupportsRestartAfterStop, nameof(ReminderTableCapabilities.SupportsRestartAfterStop)))
        {
            return;
        }

        var grainId = NewGrainId("restart");
        var entry = NewEntry(grainId, "restart");
        var etag = await UpsertAsync(entry, Guarantee);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await ReminderTable.StopAsync(cancellation.Token);
        await ReminderTable.StartAsync(cancellation.Token);

        var reread = await ReadUntilAsync(
            () => ReminderTable.ReadRow(grainId, entry.ReminderName),
            value => value is not null && EntryMatches(entry, etag, value),
            Guarantee,
            "ReadRow",
            $"the restarted table to return {Describe(entry)} with ETag {FormatETag(etag)}");
        if (reread is null)
        {
            Report(Guarantee, "ReadRow")
                .WithIdentity(grainId, entry.ReminderName)
                .WithExpected("the reminder to survive a stop/start cycle")
                .WithObserved("ReadRow returned null")
                .WithETags(etag)
                .WithSchedule(entry.StartAt, entry.Period)
                .Throw();
        }

        AssertEntry(Guarantee, "ReadRow", entry, etag, reread!);
        await RemoveAsync(grainId, entry.ReminderName, reread!.ETag!);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Upsert, point read, grain read, identity
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Guarantee: a successful upsert returns a non-empty ETag.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_UpsertRow_ReturnsNewNonEmptyETag()
    {
        const string Guarantee = nameof(ReminderTable_UpsertRow_ReturnsNewNonEmptyETag);

        var grainId = NewGrainId("upsert-etag");
        await UpsertAsync(NewEntry(grainId, "upsert-etag"), Guarantee);
        var second = await UpsertAsync(NewEntry(grainId, "upsert-etag", BaseTime.AddMinutes(5)), Guarantee);

        await RemoveAsync(grainId, "upsert-etag", second);
    }

    /// <summary>
    /// Guarantee: a point read returns the persisted identity, schedule and ETag of an upserted reminder.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_UpsertRow_PersistsScheduleForPointRead()
    {
        const string Guarantee = nameof(ReminderTable_UpsertRow_PersistsScheduleForPointRead);

        var grainId = NewGrainId("point-read");
        var entry = NewEntry(grainId, "foo/bar\\#b_a_z?", BaseTime.AddMinutes(7), TimeSpan.FromMinutes(3));
        var etag = await UpsertAsync(entry, Guarantee);

        var read = await ReadRequiredAsync(grainId, entry.ReminderName, Guarantee, entry, etag);
        AssertEntry(Guarantee, "ReadRow", entry, etag, read);

        await RemoveAsync(grainId, entry.ReminderName, etag);
    }

    /// <summary>
    /// Guarantee: a point read of an unknown reminder returns <see langword="null"/> rather than a default entry.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_ReadRow_MissingReminder_ReturnsNull()
    {
        const string Guarantee = nameof(ReminderTable_ReadRow_MissingReminder_ReturnsNull);

        var grainId = NewGrainId("missing-point-read");
        var read = await ReminderTable.ReadRow(grainId, "never-registered");
        if (read is not null)
        {
            Report(Guarantee, "ReadRow")
                .WithIdentity(grainId, "never-registered")
                .WithExpected("null for a reminder which was never upserted")
                .WithObserved($"entry {Describe(read)}")
                .WithETags(read.ETag)
                .WithSchedule(read.StartAt, read.Period)
                .Throw();
        }
    }

    /// <summary>
    /// Guarantee: the grain-scoped read returns every reminder of the requested grain and no reminder of any other grain.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders()
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders);

        var target = NewGrainId("grain-read-target");
        var other = NewGrainId("grain-read-other");

        var first = NewEntry(target, "alpha", BaseTime, TimeSpan.FromMinutes(1));
        var second = NewEntry(target, "beta", BaseTime.AddMinutes(1), TimeSpan.FromMinutes(2));
        var otherEntry = NewEntry(other, "alpha", BaseTime, TimeSpan.FromMinutes(1));
        var firstETag = await UpsertAsync(first, Guarantee);
        var secondETag = await UpsertAsync(second, Guarantee);
        var otherETag = await UpsertAsync(otherEntry, Guarantee);

        var rows = await ReadUntilAsync(
            () => ReminderTable.ReadRows(target),
            value => value is not null && value.Reminders.Count == 2,
            Guarantee,
            "ReadRows(GrainId)",
            "both reminders written for the target grain");
        var requiredRows = RequireRows(Guarantee, "ReadRows(GrainId)", rows);
        AssertExactEntries(
            Guarantee,
            "ReadRows(GrainId)",
            [
                ReminderTableEntrySnapshot.Create(first, firstETag, Capabilities.SupportsSubSecondPrecision),
                ReminderTableEntrySnapshot.Create(second, secondETag, Capabilities.SupportsSubSecondPrecision)
            ],
            requiredRows.Reminders);

        foreach (var reminder in requiredRows.Reminders)
        {
            if (!reminder.GrainId.Equals(target))
            {
                Report(Guarantee, "ReadRows(GrainId)")
                    .WithIdentity(reminder.GrainId, reminder.ReminderName)
                    .WithExpected($"only reminders owned by {target}")
                    .WithObserved($"a reminder owned by {reminder.GrainId}")
                    .WithETags(reminder.ETag)
                    .Throw();
            }
        }

        await RemoveAsync(target, "alpha", firstETag);
        await RemoveAsync(target, "beta", secondETag);
        await RemoveAsync(other, "alpha", otherETag);
    }

    /// <summary>
    /// Guarantee: the grain-scoped read of a grain with no reminders returns an empty, non-null result.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty()
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty);

        var grainId = NewGrainId("grain-read-unknown");
        var rows = await ReadRowsUntilExactAsync(
            () => ReminderTable.ReadRows(grainId),
            [],
            Guarantee,
            "ReadRows(GrainId)");
        if (rows.Reminders.Count != 0)
        {
            Report(Guarantee, "ReadRows(GrainId)")
                .WithIdentity(grainId, null)
                .WithExpected("an empty result for a grain with no reminders")
                .WithObserved($"{rows.Reminders.Count} reminders: {string.Join(", ", rows.Reminders.Select(Describe))}")
                .Throw();
        }
    }

    /// <summary>
    /// Guarantee: reminder identity is the pair (<see cref="ReminderEntry.GrainId"/>, <see cref="ReminderEntry.ReminderName"/>).
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_Identity_IsGrainIdAndReminderName()
    {
        const string Guarantee = nameof(ReminderTable_Identity_IsGrainIdAndReminderName);

        var grainA = NewGrainId("identity-a");
        var grainB = NewGrainId("identity-b");

        var aFirst = NewEntry(grainA, "shared-name", BaseTime, TimeSpan.FromMinutes(1));
        var aSecond = NewEntry(grainA, "other-name", BaseTime.AddMinutes(2), TimeSpan.FromMinutes(2));
        var bFirst = NewEntry(grainB, "shared-name", BaseTime.AddMinutes(4), TimeSpan.FromMinutes(3));

        var aFirstETag = await UpsertAsync(aFirst, Guarantee);
        var aSecondETag = await UpsertAsync(aSecond, Guarantee);
        var bFirstETag = await UpsertAsync(bFirst, Guarantee);

        AssertEntry(Guarantee, "ReadRow", aFirst, aFirstETag, await ReadRequiredAsync(grainA, "shared-name", Guarantee, aFirst, aFirstETag));
        AssertEntry(Guarantee, "ReadRow", aSecond, aSecondETag, await ReadRequiredAsync(grainA, "other-name", Guarantee, aSecond, aSecondETag));
        AssertEntry(Guarantee, "ReadRow", bFirst, bFirstETag, await ReadRequiredAsync(grainB, "shared-name", Guarantee, bFirst, bFirstETag));

        // Removing one identity must not affect the two identities which share one component with it.
        if (!await ReminderTable.RemoveRow(grainA, "shared-name", aFirstETag))
        {
            Report(Guarantee, "RemoveRow")
                .WithIdentity(grainA, "shared-name")
                .WithExpected("removal with the current ETag to succeed")
                .WithObserved("RemoveRow returned false")
                .WithETags(aFirstETag, supplied: aFirstETag)
                .Throw();
        }

        AssertEntry(Guarantee, "ReadRow", aSecond, aSecondETag, await ReadRequiredAsync(grainA, "other-name", Guarantee, aSecond, aSecondETag));
        AssertEntry(Guarantee, "ReadRow", bFirst, bFirstETag, await ReadRequiredAsync(grainB, "shared-name", Guarantee, bFirst, bFirstETag));

        await RemoveAsync(grainA, "other-name", aSecondETag);
        await RemoveAsync(grainB, "shared-name", bFirstETag);
    }

    /// <summary>
    /// Guarantee: reminder names containing path, escape, fragment, and query characters round-trip unchanged.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_Identity_WithSpecialCharacters_RoundTrips()
    {
        const string Guarantee = nameof(ReminderTable_Identity_WithSpecialCharacters_RoundTrips);
        const string ReminderName = "foo/bar\\#b_a_z?";
        var grainId = NewGrainId("special-characters");
        var expected = NewEntry(grainId, ReminderName);
        var etag = await UpsertAsync(expected, Guarantee);

        AssertEntry(Guarantee, "ReadRow", expected, etag, await ReadRequiredAsync(grainId, ReminderName, Guarantee, expected, etag));
        await RemoveAsync(grainId, ReminderName, etag);
    }

    // ---------------------------------------------------------------------------------------------------------
    // ETag semantics
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Guarantee: every successful upsert replaces the ETag, and the point read observes the newest ETag.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_UpsertRow_ReplacesETagOnEachWrite()
    {
        const string Guarantee = nameof(ReminderTable_UpsertRow_ReplacesETagOnEachWrite);
        if (!Require(Guarantee, Capabilities.SupportsETagRotation, nameof(ReminderTableCapabilities.SupportsETagRotation)))
        {
            return;
        }

        var grainId = NewGrainId("etag-replace");
        var observed = new List<string>();
        var previous = (string?)null;

        for (var i = 0; i < 3; i++)
        {
            var entry = NewEntry(grainId, "etag-replace", BaseTime.AddMinutes(i), TimeSpan.FromMinutes(1 + i));
            var etag = await UpsertAsync(entry, Guarantee);
            if (previous is not null && string.Equals(previous, etag, StringComparison.Ordinal))
            {
                Report(Guarantee, "UpsertRow")
                    .WithIdentity(grainId, "etag-replace")
                    .WithExpected($"write #{i + 1} to return a fresh ETag")
                    .WithObserved($"the ETag {FormatETag(etag)} was reused")
                    .WithETags(etag, previous)
                    .WithSchedule(entry.StartAt, entry.Period)
                    .Throw();
            }

            observed.Add(etag);
            previous = etag;

            var read = await ReadRequiredAsync(grainId, "etag-replace", Guarantee, entry, etag);
            if (!string.Equals(read.ETag, etag, StringComparison.Ordinal))
            {
                Report(Guarantee, "ReadRow")
                    .WithIdentity(grainId, "etag-replace")
                    .WithExpected($"the point read to observe the newest ETag {FormatETag(etag)}")
                    .WithObserved($"the point read returned {FormatETag(read.ETag)}")
                    .WithETags(read.ETag, previous, etag)
                    .WithSchedule(read.StartAt, read.Period)
                    .Throw();
            }
        }

        if (observed.Distinct(StringComparer.Ordinal).Count() != observed.Count)
        {
            Report(Guarantee, "UpsertRow")
                .WithIdentity(grainId, "etag-replace")
                .WithExpected("three distinct ETags across three writes")
                .WithObserved($"[{string.Join(", ", observed)}]")
                .Throw();
        }

        await RemoveAsync(grainId, "etag-replace", observed[^1]);
    }

    /// <summary>
    /// Guarantee: conditional removal with the current ETag removes the row.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_RemoveRow_WithCurrentETag_RemovesRow()
    {
        const string Guarantee = nameof(ReminderTable_RemoveRow_WithCurrentETag_RemovesRow);

        var grainId = NewGrainId("remove-current");
        var entry = NewEntry(grainId, "remove-current");
        var etag = await UpsertAsync(entry, Guarantee);

        var removed = await ReminderTable.RemoveRow(grainId, entry.ReminderName, etag);
        if (!removed)
        {
            Report(Guarantee, "RemoveRow")
                .WithIdentity(grainId, entry.ReminderName)
                .WithExpected("true when removing with the current ETag")
                .WithObserved("RemoveRow returned false")
                .WithETags(etag, supplied: etag)
                .WithSchedule(entry.StartAt, entry.Period)
                .Throw();
        }

        var read = await ReadUntilAsync(
            () => ReminderTable.ReadRow(grainId, entry.ReminderName),
            static value => value is null,
            Guarantee,
            "ReadRow",
            "null after successful removal");
        if (read is not null)
        {
            Report(Guarantee, "ReadRow")
                .WithIdentity(grainId, entry.ReminderName)
                .WithExpected("null after a successful conditional removal")
                .WithObserved($"entry {Describe(read)}")
                .WithETags(read.ETag, supplied: etag)
                .Throw();
        }
    }

    /// <summary>
    /// Guarantee: conditional removal with a stale ETag fails and leaves the current row untouched.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow()
    {
        const string Guarantee = nameof(ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow);
        if (!Require(Guarantee, Capabilities.SupportsETagRotation, nameof(ReminderTableCapabilities.SupportsETagRotation)))
        {
            return;
        }

        var grainId = NewGrainId("remove-stale");
        var staleETag = await UpsertAsync(NewEntry(grainId, "remove-stale", BaseTime, TimeSpan.FromMinutes(1)), Guarantee);
        var updated = NewEntry(grainId, "remove-stale", BaseTime.AddMinutes(9), TimeSpan.FromMinutes(4));
        var currentETag = await UpsertAsync(updated, Guarantee);

        var removed = await ReminderTable.RemoveRow(grainId, "remove-stale", staleETag);
        if (removed)
        {
            Report(Guarantee, "RemoveRow")
                .WithIdentity(grainId, "remove-stale")
                .WithExpected("false when removing with a stale ETag")
                .WithObserved("RemoveRow returned true and deleted the row")
                .WithETags(currentETag, staleETag, staleETag)
                .WithSchedule(updated.StartAt, updated.Period)
                .Throw();
        }

        var read = await ReadRequiredAsync(grainId, "remove-stale", Guarantee, updated, currentETag);
        AssertEntry(Guarantee, "ReadRow", updated, currentETag, read);

        await RemoveAsync(grainId, "remove-stale", currentETag);
    }

    /// <summary>
    /// Guarantee: removal targeting an unknown reminder name returns <see langword="false"/> and removes nothing.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse()
    {
        const string Guarantee = nameof(ReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse);

        var grainId = NewGrainId("remove-unknown");
        var entry = NewEntry(grainId, "present");
        var etag = await UpsertAsync(entry, Guarantee);

        var removed = await ReminderTable.RemoveRow(grainId, "absent", etag);
        if (removed)
        {
            Report(Guarantee, "RemoveRow")
                .WithIdentity(grainId, "absent")
                .WithExpected("false when the reminder name does not exist")
                .WithObserved("RemoveRow returned true")
                .WithETags(etag, supplied: etag)
                .Throw();
        }

        AssertEntry(Guarantee, "ReadRow", entry, etag, await ReadRequiredAsync(grainId, "present", Guarantee, entry, etag));
        await RemoveAsync(grainId, "present", etag);
    }

    /// <summary>
    /// Guarantee: a repeated removal of an already removed reminder returns <see langword="false"/>.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess()
    {
        const string Guarantee = nameof(ReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess);

        var grainId = NewGrainId("remove-twice");
        var entry = NewEntry(grainId, "remove-twice");
        var etag = await UpsertAsync(entry, Guarantee);

        var first = await ReminderTable.RemoveRow(grainId, entry.ReminderName, etag);
        var second = await ReminderTable.RemoveRow(grainId, entry.ReminderName, etag);

        if (!first || second)
        {
            Report(Guarantee, "RemoveRow")
                .WithIdentity(grainId, entry.ReminderName)
                .WithExpected("the first removal to return true and the second to return false")
                .WithObserved($"first={first.ToString(CultureInfo.InvariantCulture)}, second={second.ToString(CultureInfo.InvariantCulture)}")
                .WithETags(etag, supplied: etag)
                .Throw();
        }
    }

    /// <summary>
    /// Guarantee: an upsert carrying a stale ETag is rejected by providers which implement conditional upsert.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_UpsertRow_WithStaleETag_IsRejected()
    {
        const string Guarantee = nameof(ReminderTable_UpsertRow_WithStaleETag_IsRejected);
        if (!Require(Guarantee, Capabilities.SupportsConditionalUpsert, nameof(ReminderTableCapabilities.SupportsConditionalUpsert)))
        {
            return;
        }

        var grainId = NewGrainId("conditional-upsert");
        var staleETag = await UpsertAsync(NewEntry(grainId, "conditional-upsert", BaseTime, TimeSpan.FromMinutes(1)), Guarantee);
        var current = NewEntry(grainId, "conditional-upsert", BaseTime.AddMinutes(3), TimeSpan.FromMinutes(2));
        var currentETag = await ReminderTableConvergence.ReadUntilAsync(
            () => UpsertAsync(current, Guarantee),
            etag => !string.Equals(etag, staleETag, StringComparison.Ordinal),
            Capabilities,
            Guarantee,
            "UpsertRow",
            "a current ETag which supersedes the stale ETag",
            FormatETag);
        if (string.Equals(currentETag, staleETag, StringComparison.Ordinal))
        {
            Report(Guarantee, "UpsertRow")
                .WithIdentity(grainId, "conditional-upsert")
                .WithExpected("a current ETag which supersedes the stale ETag")
                .WithObserved($"both writes returned {FormatETag(currentETag)}")
                .WithETags(currentETag, staleETag)
                .WithSchedule(current.StartAt, current.Period)
                .Throw();
        }

        var rejected = NewEntry(grainId, "conditional-upsert", BaseTime.AddMinutes(6), TimeSpan.FromMinutes(8));
        rejected.ETag = staleETag;

        Exception? failure = null;
        string? returnedETag = null;
        try
        {
            returnedETag = await ReminderTable.UpsertRow(rejected);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is null && returnedETag is not null)
        {
            Report(Guarantee, "UpsertRow")
                .WithIdentity(grainId, "conditional-upsert")
                .WithExpected("a conditional upsert carrying a stale ETag to return null or throw")
                .WithObserved($"the upsert succeeded and returned {FormatETag(returnedETag)}")
                .WithETags(currentETag, staleETag, staleETag)
                .WithSchedule(rejected.StartAt, rejected.Period)
                .Throw();
        }

        AssertEntry(Guarantee, "ReadRow", current, currentETag, await ReadRequiredAsync(grainId, "conditional-upsert", Guarantee, current, currentETag));
        await RemoveAsync(grainId, "conditional-upsert", currentETag);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Schedule updates and window movement
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Guarantee: an upsert on an existing identity updates <see cref="ReminderEntry.StartAt"/> and
    /// <see cref="ReminderEntry.Period"/> in place rather than creating a second row.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_UpsertRow_UpdatesStartAtAndPeriod()
    {
        const string Guarantee = nameof(ReminderTable_UpsertRow_UpdatesStartAtAndPeriod);

        var grainId = NewGrainId("schedule-update");
        await UpsertAsync(NewEntry(grainId, "schedule-update", BaseTime, TimeSpan.FromMinutes(1)), Guarantee);

        var updated = NewEntry(grainId, "schedule-update", BaseTime.AddHours(2), TimeSpan.FromMinutes(17));
        var updatedETag = await UpsertAsync(updated, Guarantee);

        AssertEntry(Guarantee, "ReadRow", updated, updatedETag, await ReadRequiredAsync(grainId, "schedule-update", Guarantee, updated, updatedETag));

        var rows = await ReadRowsUntilExactAsync(
            () => ReminderTable.ReadRows(grainId),
            [ReminderTableEntrySnapshot.Create(updated, updatedETag, Capabilities.SupportsSubSecondPrecision)],
            Guarantee,
            "ReadRows(GrainId)");
        if (rows.Reminders.Count != 1)
        {
            Report(Guarantee, "ReadRows(GrainId)")
                .WithIdentity(grainId, "schedule-update")
                .WithExpected("an update to replace the existing row rather than add a new one")
                .WithObserved($"{rows.Reminders.Count} rows: {string.Join(", ", rows.Reminders.Select(Describe))}")
                .WithSchedule(updated.StartAt, updated.Period)
                .Throw();
        }

        await RemoveAsync(grainId, "schedule-update", updatedETag);
    }

    /// <summary>
    /// Guarantee: moving a reminder's start time across a loading window boundary is observable through both the
    /// point read and the grain-scoped read, and the previous schedule is no longer visible.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows()
    {
        const string Guarantee = nameof(ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows);

        var window = TimeSpan.FromMinutes(10);
        var grainId = NewGrainId("window-move");

        var inside = NewEntry(grainId, "window-move", BaseTime.AddMinutes(2), TimeSpan.FromMinutes(30));
        var insideETag = await UpsertAsync(inside, Guarantee);
        if (!IsWithinWindow(inside.StartAt, BaseTime, window))
        {
            Report(Guarantee, "UpsertRow")
                .WithIdentity(grainId, "window-move")
                .WithExpected("the fixture start time to fall inside the loading window")
                .WithObserved($"StartAt={inside.StartAt:O}")
                .WithSchedule(inside.StartAt, inside.Period)
                .WithWindow(BaseTime, window)
                .Throw();
        }

        var outside = NewEntry(grainId, "window-move", BaseTime.AddHours(3), TimeSpan.FromMinutes(30));
        var outsideETag = await UpsertAsync(outside, Guarantee);

        var read = await ReadRequiredAsync(grainId, "window-move", Guarantee, outside, outsideETag);
        AssertEntry(Guarantee, "ReadRow", outside, outsideETag, read);

        var expectedRows = new[]
        {
            ReminderTableEntrySnapshot.Create(outside, outsideETag, Capabilities.SupportsSubSecondPrecision)
        };
        var rows = await ReadRowsUntilExactAsync(
            () => ReminderTable.ReadRows(0, 0),
            expectedRows,
            Guarantee,
            "ReadRows(0, 0)");
        AssertExactEntries(Guarantee, "ReadRows(0, 0)", expectedRows, rows.Reminders);

        var enumerated = rows.Reminders[0];
        if (IsWithinWindow(enumerated.StartAt, BaseTime, window))
        {
            Report(Guarantee, "ReadRows(0, 0)")
                .WithIdentity(grainId, "window-move")
                .WithExpected("the reminder to have moved outside the loading window")
                .WithObserved($"the enumerated StartAt={enumerated.StartAt:O} is still inside it")
                .WithETags(outsideETag, insideETag)
                .WithSchedule(enumerated.StartAt, enumerated.Period)
                .WithWindow(BaseTime, window)
                .Throw();
        }

        await RemoveAsync(grainId, "window-move", outsideETag);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Hash range semantics
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Guarantee: the degenerate range <c>(0, 0]</c> enumerates every reminder in the table.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_ReadRows_FullRange_ReturnsAllReminders()
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_FullRange_ReturnsAllReminders);

        var fixtureState = await CreateRangeFixtureAsync(Guarantee);
        try
        {
            var full = await ReadRangeUntilExactAsync(
                () => ReminderTable.ReadRows(0, 0),
                fixtureState.All,
                Guarantee,
                "ReadRows(0, 0)");
            AssertRange(Guarantee, "ReadRows(0, 0)", 0, 0, fixtureState, full, fixtureState.All, []);

        }
        finally
        {
            await fixtureState.CleanupAsync();
        }
    }

    /// <summary>
    /// Guarantee: the unsigned upper ring boundary <c>(0, uint.MaxValue]</c> excludes only hash zero.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering()
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering);
        if (!Require(
            Guarantee,
            Capabilities.SupportsUnsignedHashRangeBoundaries,
            nameof(ReminderTableCapabilities.SupportsUnsignedHashRangeBoundaries)))
        {
            return;
        }

        var fixtureState = await CreateRangeFixtureAsync(Guarantee);
        try
        {
            var expected = fixtureState.All.Where(item => item.Hash != 0).ToList();
            var excluded = fixtureState.All.Where(item => item.Hash == 0).ToList();
            var bounded = await ReadRangeUntilExactAsync(
                () => ReminderTable.ReadRows(0, uint.MaxValue),
                expected,
                Guarantee,
                "ReadRows(0, uint.MaxValue)");
            AssertRange(Guarantee, "ReadRows(0, uint.MaxValue)", 0, uint.MaxValue, fixtureState, bounded, expected, excluded);
        }
        finally
        {
            await fixtureState.CleanupAsync();
        }
    }

    /// <summary>
    /// Guarantee: the full range returns exactly the requested number of distinct, complete reminder entries.
    /// </summary>
    /// <param name="reminderCount">The exact positive number of reminders to create and enumerate.</param>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality(int reminderCount)
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality);

        if (reminderCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reminderCount), reminderCount, "The requested reminder count must be positive.");
        }

        var batchSize = Math.Max(1, Capabilities.CardinalityMutationBatchSize);
        var pending = new List<(ReminderEntry Entry, Task<string?> Upsert)>(batchSize);
        var created = new List<(ReminderEntry Entry, string ETag)>(reminderCount);
        try
        {
            for (var index = 0; index < reminderCount; index++)
            {
                var entry = NewEntry(
                    NewGrainId($"requested-cardinality-{index}"),
                    $"requested-cardinality-{index}",
                    BaseTime.AddMinutes(index),
                    TimeSpan.FromMinutes((index % 17) + 1));
                pending.Add((entry, ReminderTable.UpsertRow(entry)));

                if (pending.Count == batchSize || index == reminderCount - 1)
                {
                    await CompleteUpsertBatchAsync(pending, created, Guarantee);
                }
            }

            var expected = created.Select(item => ReminderTableEntrySnapshot.Create(
                    item.Entry,
                    item.ETag,
                    Capabilities.SupportsSubSecondPrecision)).ToList();
            var rows = await ReadRowsUntilExactAsync(
                () => ReminderTable.ReadRows(0, 0),
                expected,
                Guarantee,
                "ReadRows(0, 0)");
            AssertExactEntries(Guarantee, "ReadRows(0, 0)", expected, rows.Reminders);
        }
        finally
        {
            for (var offset = 0; offset < created.Count; offset += batchSize)
            {
                var batch = created.Skip(offset).Take(batchSize);
                await Task.WhenAll(batch.Select(item => RemoveAsync(item.Entry.GrainId, item.Entry.ReminderName, item.ETag)));
            }
        }
    }

    /// <summary>
    /// Guarantee: a non-wrapping range is exclusive of <c>begin</c> and inclusive of <c>end</c>.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd()
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd);
        if (!Require(
            Guarantee,
            Capabilities.SupportsUnsignedHashRangeBoundaries,
            nameof(ReminderTableCapabilities.SupportsUnsignedHashRangeBoundaries)))
        {
            return;
        }

        var fixtureState = await CreateRangeFixtureAsync(Guarantee);
        try
        {
            var low = fixtureState.All[0];
            var middle = fixtureState.All[1];
            var high = fixtureState.All[2];

            var rows = await ReadRangeUntilExactAsync(
                () => ReminderTable.ReadRows(low.Hash, middle.Hash),
                [middle],
                Guarantee,
                "ReadRows(low, middle)");
            AssertRange(Guarantee, "ReadRows(low, middle)", low.Hash, middle.Hash, fixtureState, rows, [middle], [low, high]);

            var upper = await ReadRangeUntilExactAsync(
                () => ReminderTable.ReadRows(middle.Hash, high.Hash),
                [high],
                Guarantee,
                "ReadRows(middle, high)");
            AssertRange(Guarantee, "ReadRows(middle, high)", middle.Hash, high.Hash, fixtureState, upper, [high], [low, middle]);
        }
        finally
        {
            await fixtureState.CleanupAsync();
        }
    }

    /// <summary>
    /// Guarantee: a wrap-around range where <c>begin &gt;= end</c> returns the union of both ring segments.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment()
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment);
        if (!Require(
            Guarantee,
            Capabilities.SupportsUnsignedHashRangeBoundaries,
            nameof(ReminderTableCapabilities.SupportsUnsignedHashRangeBoundaries)))
        {
            return;
        }

        var fixtureState = await CreateRangeFixtureAsync(Guarantee);
        try
        {
            var low = fixtureState.All[0];
            var middle = fixtureState.All[1];
            var high = fixtureState.All[2];

            // (high, low] wraps through zero: it contains 'low' and excludes 'middle' and 'high'.
            var wrapped = await ReadRangeUntilExactAsync(
                () => ReminderTable.ReadRows(high.Hash, low.Hash),
                [low],
                Guarantee,
                "ReadRows(high, low)");
            AssertRange(Guarantee, "ReadRows(high, low)", high.Hash, low.Hash, fixtureState, wrapped, [low], [middle, high]);

            // (middle, low] wraps as well and contains 'high' (above begin) and 'low' (at or below end).
            var wrappedUnion = await ReadRangeUntilExactAsync(
                () => ReminderTable.ReadRows(middle.Hash, low.Hash),
                [low, high],
                Guarantee,
                "ReadRows(middle, low)");
            AssertRange(Guarantee, "ReadRows(middle, low)", middle.Hash, low.Hash, fixtureState, wrappedUnion, [low, high], [middle]);
        }
        finally
        {
            await fixtureState.CleanupAsync();
        }
    }

    /// <summary>
    /// Guarantee: absence from a hash-range read does not remove or otherwise modify the durable reminder.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder()
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder);
        var grainId = NewGrainId("outside-range");
        var expected = NewEntry(grainId, "outside-range");
        var etag = await UpsertAsync(expected, Guarantee);
        var hash = grainId.GetUniformHashCode();
        var end = unchecked(hash + 1);

        var rows = RequireRows(Guarantee, "ReadRows(hash, hash + 1)", await ReminderTable.ReadRows(hash, end));
        if (rows.Reminders.Any(reminder => reminder.GrainId.Equals(grainId) && string.Equals(reminder.ReminderName, expected.ReminderName, StringComparison.Ordinal)))
        {
            Report(Guarantee, "ReadRows(hash, hash + 1)")
                .WithIdentity(grainId, expected.ReminderName)
                .WithRange(hash, end)
                .WithExpected("the reminder at the exclusive lower bound to be absent")
                .WithObserved("the range read returned the reminder")
                .Throw();
        }

        AssertEntry(Guarantee, "ReadRow", expected, etag, await ReadRequiredAsync(grainId, expected.ReminderName, Guarantee, expected, etag));
        await RemoveAsync(grainId, expected.ReminderName, etag);
    }

    /// <summary>
    /// Guarantee: a removed reminder disappears from range enumeration while its siblings remain.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder()
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder);

        var fixtureState = await CreateRangeFixtureAsync(Guarantee);
        try
        {
            var removed = fixtureState.All[1];
            if (!await ReminderTable.RemoveRow(removed.GrainId, removed.ReminderName, removed.ETag))
            {
                Report(Guarantee, "RemoveRow")
                    .WithIdentity(removed.GrainId, removed.ReminderName)
                    .WithExpected("removal with the current ETag to succeed")
                    .WithObserved("RemoveRow returned false")
                    .WithETags(removed.ETag, supplied: removed.ETag)
                    .Throw();
            }

            fixtureState.MarkRemoved(removed);

            var remaining = fixtureState.All.Where(item => !item.Removed).ToList();
            var rows = await ReadRangeUntilExactAsync(
                () => ReminderTable.ReadRows(0, 0),
                remaining,
                Guarantee,
                "ReadRows(0, 0)");
            AssertRange(Guarantee, "ReadRows(0, 0)", 0, 0, fixtureState, rows, remaining, [removed]);
        }
        finally
        {
            await fixtureState.CleanupAsync();
        }
    }

    /// <summary>
    /// Guarantee: deletion is confirmed by an explicit point read returning <see langword="null"/>. Absence from a
    /// range or due-time enumeration alone must never be treated as durable deletion.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_ReadRow_AfterRemoval_ReturnsNull()
    {
        const string Guarantee = nameof(ReminderTable_ReadRow_AfterRemoval_ReturnsNull);

        var grainId = NewGrainId("deletion-observation");
        var entry = NewEntry(grainId, "deletion-observation", BaseTime.AddMinutes(11), TimeSpan.FromMinutes(6));
        var etag = await UpsertAsync(entry, Guarantee);
        var hash = grainId.GetUniformHashCode();

        // A range which cannot contain this reminder proves absence from a page is not deletion.
        var disjointBegin = unchecked(hash + 1);
        var disjointEnd = unchecked(hash + 2);
        var disjoint = RequireRows(Guarantee, "ReadRows(disjoint)", await ReminderTable.ReadRows(disjointBegin, disjointEnd));
        if (disjoint.Reminders.Any(reminder => reminder.GrainId.Equals(grainId) && string.Equals(reminder.ReminderName, entry.ReminderName, StringComparison.Ordinal)))
        {
            Report(Guarantee, "ReadRows(begin, end)")
                .WithIdentity(grainId, entry.ReminderName)
                .WithExpected("the reminder to be absent from a range which excludes its hash")
                .WithObserved("the reminder was returned")
                .WithRange(disjointBegin, disjointEnd)
                .WithOwnership("reminder", [hash])
                .Throw();
        }

        var stillPresent = await ReadUntilAsync(
            () => ReminderTable.ReadRow(grainId, entry.ReminderName),
            value => value is not null && EntryMatches(entry, etag, value),
            Guarantee,
            "ReadRow",
            "the row to remain durably present after an excluding range read");
        if (stillPresent is null)
        {
            Report(Guarantee, "ReadRow")
                .WithIdentity(grainId, entry.ReminderName)
                .WithExpected("absence from a range page to leave the row durably present")
                .WithObserved("the point read returned null")
                .WithETags(etag)
                .WithRange(disjointBegin, disjointEnd)
                .WithSchedule(entry.StartAt, entry.Period)
                .Throw();
        }

        await RemoveAsync(grainId, entry.ReminderName, etag);

        var afterRemoval = await ReadUntilAsync(
            () => ReminderTable.ReadRow(grainId, entry.ReminderName),
            static value => value is null,
            Guarantee,
            "ReadRow",
            "null after successful removal");
        if (afterRemoval is not null)
        {
            Report(Guarantee, "ReadRow")
                .WithIdentity(grainId, entry.ReminderName)
                .WithExpected("null once the row has actually been removed")
                .WithObserved($"entry {Describe(afterRemoval)}")
                .WithETags(afterRemoval.ETag, etag, etag)
                .Throw();
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Duplicate and concurrent operations
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Guarantee: concurrent upserts of one identity all succeed and each returns a distinct ETag.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_ConcurrentUpserts_ProduceDistinctETags()
    {
        const string Guarantee = nameof(ReminderTable_ConcurrentUpserts_ProduceDistinctETags);
        if (!Require(
            Guarantee,
            Capabilities.SupportsSameIdentityConcurrentUpserts,
            nameof(ReminderTableCapabilities.SupportsSameIdentityConcurrentUpserts)))
        {
            return;
        }

        var count = Math.Max(2, Capabilities.ConcurrentUpsertCount);
        var grainId = NewGrainId("concurrent-upsert");

        var etags = await Task.WhenAll(Enumerable.Range(0, count).Select(index =>
            ReminderTable.UpsertRow(NewEntry(grainId, "concurrent-upsert", BaseTime.AddSeconds(index), TimeSpan.FromMinutes(1)))));

        var distinct = etags.Where(etag => !string.IsNullOrEmpty(etag)).Distinct(StringComparer.Ordinal).Count();
        if (distinct != count)
        {
            Report(Guarantee, "UpsertRow")
                .WithIdentity(grainId, "concurrent-upsert")
                .WithExpected($"{count} distinct ETags from {count} concurrent upserts")
                .WithObserved($"{distinct} distinct ETags: [{string.Join(", ", etags.Select(FormatETag))}]")
                .Throw();
        }

        var read = await ReadRequiredAsync(grainId, "concurrent-upsert", Guarantee);
        if (!etags.Contains(read.ETag, StringComparer.Ordinal))
        {
            Report(Guarantee, "ReadRow")
                .WithIdentity(grainId, "concurrent-upsert")
                .WithExpected("the stored ETag to be one returned by the concurrent writes")
                .WithObserved($"stored ETag {FormatETag(read.ETag)}; returned [{string.Join(", ", etags.Select(FormatETag))}]")
                .WithETags(read.ETag)
                .Throw();
        }

        var rows = RequireRows(
            Guarantee,
            "ReadRows(GrainId)",
            await ReadUntilAsync(
                () => ReminderTable.ReadRows(grainId),
                value => value is not null && value.Reminders.Count == 1,
                Guarantee,
                "ReadRows(GrainId)",
                "exactly one durable row for one reminder identity"));
        if (rows.Reminders.Count != 1)
        {
            Report(Guarantee, "ReadRows(GrainId)")
                .WithIdentity(grainId, "concurrent-upsert")
                .WithExpected("exactly one durable row for one reminder identity")
                .WithObserved($"{rows.Reminders.Count} rows: {string.Join(", ", rows.Reminders.Select(Describe))}")
                .Throw();
        }

        await RemoveAsync(grainId, "concurrent-upsert", read.ETag!);
    }

    /// <summary>
    /// Guarantee: parallel upserts across distinct grains remain isolated: every grain observes exactly its own
    /// reminders and its own ETag stream.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated()
    {
        const string Guarantee = nameof(ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated);
        if (!Require(
            Guarantee,
            Capabilities.SupportsParallelDistinctRows,
            nameof(ReminderTableCapabilities.SupportsParallelDistinctRows)))
        {
            return;
        }

        var grainCount = Math.Max(2, Capabilities.ParallelGrainCount);
        var perGrain = Math.Max(2, Capabilities.ConcurrentUpsertCount);
        var grains = Enumerable.Range(0, grainCount).Select(index => NewGrainId($"parallel-{index}")).ToList();

        var results = await Task.WhenAll(grains.Select(async grainId =>
        {
            var entries = Enumerable.Range(0, perGrain)
                .Select(index => NewEntry(grainId, $"parallel-{index}", BaseTime.AddSeconds(index), TimeSpan.FromMinutes(1)))
                .ToList();
            var etags = await Task.WhenAll(entries.Select(ReminderTable.UpsertRow));
            return (GrainId: grainId, Entries: entries, ETags: etags);
        }));

        foreach (var (grainId, entries, etags) in results)
        {
            if (etags.Any(string.IsNullOrEmpty))
            {
                Report(Guarantee, "UpsertRow")
                    .WithIdentity(grainId, "parallel-*")
                    .WithExpected($"{perGrain} successful writes with non-empty ETags for distinct reminder rows")
                    .WithObserved($"ETags: [{string.Join(", ", etags.Select(FormatETag))}]")
                    .Throw();
            }

            var expected = entries.Select((entry, index) =>
                ReminderTableEntrySnapshot.Create(entry, etags[index]!, Capabilities.SupportsSubSecondPrecision)).ToList();
            var rows = await ReadRowsUntilExactAsync(
                () => ReminderTable.ReadRows(grainId),
                expected,
                Guarantee,
                "ReadRows(GrainId)");
            if (rows.Reminders.Count != perGrain || rows.Reminders.Any(reminder => !reminder.GrainId.Equals(grainId)))
            {
                Report(Guarantee, "ReadRows(GrainId)")
                    .WithIdentity(grainId, "parallel-*")
                    .WithExpected($"exactly {perGrain} reminders, all owned by {grainId}")
                    .WithObserved($"{rows.Reminders.Count} reminders: {string.Join(", ", rows.Reminders.Select(Describe))}")
                    .WithOwnership("grain", [grainId.GetUniformHashCode()])
                    .Throw();
            }

            foreach (var reminder in rows.Reminders)
            {
                await ReminderTable.RemoveRow(grainId, reminder.ReminderName, reminder.ETag!);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Clear, isolation, and cancellation
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Guarantee: <see cref="IReminderTable.TestOnlyClearTable"/> removes every reminder.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_TestOnlyClearTable_RemovesAllReminders()
    {
        const string Guarantee = nameof(ReminderTable_TestOnlyClearTable_RemovesAllReminders);
        var first = NewGrainId("clear-a");
        var second = NewGrainId("clear-b");
        await UpsertAsync(NewEntry(first, "clear-a"), Guarantee);
        await UpsertAsync(NewEntry(second, "clear-b"), Guarantee);

        await ReminderTable.TestOnlyClearTable();

        var rows = RequireRows(
            Guarantee,
            "ReadRows(0, 0)",
            await ReadUntilAsync(
                () => ReminderTable.ReadRows(0, 0),
                value => value is not null && value.Reminders.Count == 0,
                Guarantee,
                "ReadRows(0, 0)",
                "an empty table after TestOnlyClearTable"));
        if (rows.Reminders.Count != 0)
        {
            Report(Guarantee, "TestOnlyClearTable")
                .WithExpected("an empty table after TestOnlyClearTable")
                .WithObserved($"{rows.Reminders.Count} reminders: {string.Join(", ", rows.Reminders.Select(Describe))}")
                .Throw();
        }

        foreach (var (grainId, name) in new[] { (first, "clear-a"), (second, "clear-b") })
        {
            var read = await ReadUntilAsync(
                () => ReminderTable.ReadRow(grainId, name),
                static value => value is null,
                Guarantee,
                "ReadRow",
                $"null for ({grainId}, '{name}') after TestOnlyClearTable");
            if (read is not null)
            {
                Report(Guarantee, "ReadRow")
                    .WithIdentity(grainId, name)
                    .WithExpected("null after TestOnlyClearTable")
                    .WithObserved($"entry {Describe(read)}")
                    .WithETags(read.ETag)
                    .Throw();
            }
        }
    }

    /// <summary>
    /// Guarantee: two independently scoped tables (different service or cluster identity) do not share reminders.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_SeparatelyScopedTables_DoNotShareReminders()
    {
        const string Guarantee = nameof(ReminderTable_SeparatelyScopedTables_DoNotShareReminders);
        if (!Require(Guarantee, Capabilities.SupportsCrossTableIsolation, nameof(ReminderTableCapabilities.SupportsCrossTableIsolation)))
        {
            return;
        }

        var isolated = await CreateIsolatedTableAsync();
        if (isolated is null)
        {
            Report(Guarantee, "CreateIsolatedTableAsync")
                .WithExpected($"an isolated table because {nameof(ReminderTableCapabilities.SupportsCrossTableIsolation)} is enabled")
                .WithObserved("CreateIsolatedTableAsync returned null")
                .Throw();
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await isolated!.StartAsync(cancellation.Token);

        var grainId = NewGrainId("isolation");
        var entry = NewEntry(grainId, "isolation", BaseTime.AddMinutes(13), TimeSpan.FromMinutes(9));
        var etag = await UpsertAsync(entry, Guarantee);

        var foreign = await isolated.ReadRow(grainId, entry.ReminderName);
        if (foreign is not null)
        {
            Report(Guarantee, "ReadRow")
                .WithIdentity(grainId, entry.ReminderName)
                .WithExpected("an independently scoped table not to observe reminders written to the table under test")
                .WithObserved($"entry {Describe(foreign)}")
                .WithETags(etag, supplied: foreign.ETag)
                .Throw();
        }

        await RemoveAsync(grainId, entry.ReminderName, etag);
    }

    /// <summary>
    /// Guarantee: providers which observe cancellation surface it from <see cref="IReminderTable.StartAsync"/>.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual async Task ReminderTable_StartAsync_WithCanceledToken_ThrowsOperationCanceled()
    {
        const string Guarantee = nameof(ReminderTable_StartAsync_WithCanceledToken_ThrowsOperationCanceled);
        if (!Require(Guarantee, Capabilities.SupportsStartCancellation, nameof(ReminderTableCapabilities.SupportsStartCancellation)))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Exception? failure = null;
        try
        {
            await ReminderTable.StartAsync(cancellation.Token);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (failure is not OperationCanceledException)
        {
            Report(Guarantee, "StartAsync")
                .WithExpected($"{nameof(OperationCanceledException)} for an already-canceled token")
                .WithObserved(failure is null ? "StartAsync completed successfully" : $"{failure.GetType().FullName}: {failure.Message}")
                .Throw();
        }

        using var restart = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await ReminderTable.StartAsync(restart.Token);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Creates a failure report pre-populated with the provider, guarantee and operation.
    /// </summary>
    /// <param name="guarantee">The guarantee being verified.</param>
    /// <param name="operation">The operation which produced the observation.</param>
    /// <returns>The report.</returns>
    protected ReminderFailureReport Report(string guarantee, string operation)
        => ReminderFailureReport.Create(ProviderName, guarantee, operation)
            .WithDetail("seed", Seed.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Records an explicit skip when a capability-gated guarantee is disabled.
    /// </summary>
    /// <param name="guarantee">The guarantee being verified.</param>
    /// <param name="enabled">Whether the capability is enabled.</param>
    /// <param name="capabilityName">The capability which gates the guarantee.</param>
    /// <returns><see langword="true"/> when the guarantee should execute.</returns>
    protected bool Require(string guarantee, bool enabled, string capabilityName)
    {
        if (_skipped.ContainsKey(guarantee))
        {
            return false;
        }

        if (!enabled)
        {
            throw new InvalidOperationException(
                $"{nameof(ReminderTableCapabilities)}.{capabilityName} was disabled after the guarantee manifest was constructed.");
        }

        return true;
    }

    /// <summary>
    /// Creates a grain identifier which is unique to this runner instance.
    /// </summary>
    /// <param name="label">A human readable label included in the key.</param>
    /// <returns>The grain identifier.</returns>
    protected GrainId NewGrainId(string label)
    {
        var ordinal = Interlocked.Increment(ref _grainCounter);
        return GrainId.Create(
            GrainType.Create("reminder-testkit-grain"),
            GrainIdKeyExtensions.CreateGuidKey(
                ReminderTestData.CreateGuid(Seed, $"{label}/{ordinal.ToString(CultureInfo.InvariantCulture)}"),
                $"{label}/{ordinal.ToString(CultureInfo.InvariantCulture)}"));
    }

    /// <summary>
    /// Creates a reminder entry with a schedule normalized to the precision the provider guarantees.
    /// </summary>
    /// <param name="grainId">The grain identifier.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="startAt">The start time, or <see langword="null"/> for <see cref="BaseTime"/>.</param>
    /// <param name="period">The period, or <see langword="null"/> for one minute.</param>
    /// <returns>The entry.</returns>
    protected ReminderEntry NewEntry(GrainId grainId, string reminderName, DateTime? startAt = null, TimeSpan? period = null) => new()
    {
        GrainId = grainId,
        ReminderName = reminderName,
        StartAt = Normalize(startAt ?? BaseTime),
        Period = period ?? TimeSpan.FromMinutes(1)
    };

    /// <summary>
    /// Normalizes a timestamp to UTC at the precision the provider guarantees.
    /// </summary>
    /// <param name="value">The timestamp.</param>
    /// <returns>The normalized timestamp.</returns>
    protected DateTime Normalize(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return Capabilities.SupportsSubSecondPrecision
            ? utc
            : new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    /// <summary>
    /// Upserts an entry and asserts that a non-empty ETag was returned.
    /// </summary>
    /// <param name="entry">The entry to upsert.</param>
    /// <param name="guarantee">The guarantee being verified.</param>
    /// <returns>The new ETag.</returns>
    protected async Task<string> UpsertAsync(ReminderEntry entry, string guarantee)
    {
        var etag = await ReminderTable.UpsertRow(entry);
        if (string.IsNullOrEmpty(etag))
        {
            Report(guarantee, "UpsertRow")
                .WithIdentity(entry.GrainId, entry.ReminderName)
                .WithExpected("a non-empty ETag from a successful upsert")
                .WithObserved($"UpsertRow returned {FormatETag(etag)}")
                .WithETags(etag)
                .WithSchedule(entry.StartAt, entry.Period)
                .Throw();
        }

        return etag!;
    }

    /// <summary>
    /// Reads a reminder which the guarantee requires to be present.
    /// </summary>
    /// <param name="grainId">The grain identifier.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="guarantee">The guarantee being verified.</param>
    /// <returns>The reminder entry.</returns>
    protected async Task<ReminderEntry> ReadRequiredAsync(
        GrainId grainId,
        string reminderName,
        string guarantee,
        ReminderEntry? expected = null,
        string? expectedETag = null)
    {
        var read = await ReadUntilAsync(
            () => ReminderTable.ReadRow(grainId, reminderName),
            value => value is not null && (expected is null || EntryMatches(expected, expectedETag, value)),
            guarantee,
            "ReadRow",
            expected is null
                ? "the previously upserted reminder to be readable"
                : $"{Describe(expected)} with ETag {FormatETag(expectedETag)}");
        if (read is null)
        {
            Report(guarantee, "ReadRow")
                .WithIdentity(grainId, reminderName)
                .WithExpected("the previously upserted reminder to be readable")
                .WithObserved("ReadRow returned null")
                .Throw();
        }

        return read!;
    }

    private Task<T> ReadUntilAsync<T>(
        Func<Task<T>> read,
        Func<T, bool> hasConverged,
        string guarantee,
        string operation,
        string expected)
        => ReminderTableConvergence.ReadUntilAsync(
            read,
            hasConverged,
            Capabilities,
            guarantee,
            operation,
            expected,
            value => value switch
            {
                null => "null",
                ReminderEntry entry => Describe(entry),
                ReminderTableData rows => $"{rows.Reminders.Count} rows: [{string.Join(", ", rows.Reminders.Select(Describe))}]",
                _ => value.ToString() ?? "<null>"
            });

    private bool EntryMatches(ReminderEntry expected, string? expectedETag, ReminderEntry actual)
        => actual.GrainId.Equals(expected.GrainId)
            && string.Equals(actual.ReminderName, expected.ReminderName, StringComparison.Ordinal)
            && Normalize(actual.StartAt).Ticks == Normalize(expected.StartAt).Ticks
            && actual.Period == expected.Period
            && string.Equals(actual.ETag, expectedETag, StringComparison.Ordinal);

    /// <summary>
    /// Removes a reminder, ignoring failures which only affect cleanup.
    /// </summary>
    /// <param name="grainId">The grain identifier.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="etag">The current ETag.</param>
    /// <returns>A task which represents the asynchronous operation.</returns>
    protected async Task RemoveAsync(GrainId grainId, string reminderName, string etag)
    {
        try
        {
            await ReminderTable.RemoveRow(grainId, reminderName, etag);
        }
        catch (Exception)
        {
            // Cleanup is best effort: a provider-specific cleanup failure must not mask the guarantee's result.
        }
    }

    /// <summary>
    /// Asserts that a range or grain read returned a non-null result.
    /// </summary>
    /// <param name="guarantee">The guarantee being verified.</param>
    /// <param name="operation">The operation which produced the observation.</param>
    /// <param name="rows">The result.</param>
    /// <returns>The non-null result.</returns>
    protected ReminderTableData RequireRows(string guarantee, string operation, ReminderTableData? rows)
    {
        if (rows is null)
        {
            Report(guarantee, operation)
                .WithExpected($"a non-null {nameof(ReminderTableData)}")
                .WithObserved("null")
                .Throw();
        }

        return rows!;
    }

    private async Task<ReminderTableData> ReadRowsUntilExactAsync(
        Func<Task<ReminderTableData>> read,
        IReadOnlyList<ReminderTableEntrySnapshot> expected,
        string guarantee,
        string operation)
    {
        var rows = await ReadUntilAsync(
            read,
            value => value is not null
                && ReminderTableEntrySnapshotComparer.CompareExact(
                    expected,
                    value.Reminders.Select(entry => ReminderTableEntrySnapshot.Observe(entry, Capabilities.SupportsSubSecondPrecision)).ToList()) is null,
            guarantee,
            operation,
            $"exact identities and complete entries [{string.Join(", ", expected)}]");
        return RequireRows(guarantee, operation, rows);
    }

    private Task<ReminderTableData> ReadRangeUntilExactAsync(
        Func<Task<ReminderTableData>> read,
        IReadOnlyList<RangeItem> expected,
        string guarantee,
        string operation)
        => ReadRowsUntilExactAsync(
            read,
            expected.Select(item => ReminderTableEntrySnapshot.Create(
                item.Entry,
                item.ETag,
                Capabilities.SupportsSubSecondPrecision)).ToList(),
            guarantee,
            operation);

    /// <summary>
    /// Asserts that a read observed the expected identity, schedule and ETag.
    /// </summary>
    /// <param name="guarantee">The guarantee being verified.</param>
    /// <param name="operation">The operation which produced the observation.</param>
    /// <param name="expected">The expected entry.</param>
    /// <param name="expectedETag">The expected ETag.</param>
    /// <param name="actual">The observed entry.</param>
    protected void AssertEntry(string guarantee, string operation, ReminderEntry expected, string expectedETag, ReminderEntry actual)
    {
        if (!actual.GrainId.Equals(expected.GrainId) || !string.Equals(actual.ReminderName, expected.ReminderName, StringComparison.Ordinal))
        {
            Report(guarantee, operation)
                .WithIdentity(expected.GrainId, expected.ReminderName)
                .WithExpected($"identity ({expected.GrainId}, '{expected.ReminderName}')")
                .WithObserved($"identity ({actual.GrainId}, '{actual.ReminderName}')")
                .WithETags(actual.ETag, supplied: expectedETag)
                .Throw();
        }

        var expectedStart = Normalize(expected.StartAt);
        var actualStart = Normalize(actual.StartAt);
        if (expectedStart.Ticks != actualStart.Ticks)
        {
            Report(guarantee, operation)
                .WithIdentity(expected.GrainId, expected.ReminderName)
                .WithExpected($"StartAt={expectedStart:O}")
                .WithObserved($"StartAt={actualStart:O} (raw {actual.StartAt:O}, Kind={actual.StartAt.Kind})")
                .WithETags(actual.ETag, supplied: expectedETag)
                .WithSchedule(actual.StartAt, actual.Period)
                .WithDetail("precision", Capabilities.SupportsSubSecondPrecision ? "sub-second" : "whole-second")
                .Throw();
        }

        if (expected.Period != actual.Period)
        {
            Report(guarantee, operation)
                .WithIdentity(expected.GrainId, expected.ReminderName)
                .WithExpected($"Period={expected.Period}")
                .WithObserved($"Period={actual.Period}")
                .WithETags(actual.ETag, supplied: expectedETag)
                .WithSchedule(actual.StartAt, actual.Period)
                .Throw();
        }

        if (!string.Equals(actual.ETag, expectedETag, StringComparison.Ordinal))
        {
            Report(guarantee, operation)
                .WithIdentity(expected.GrainId, expected.ReminderName)
                .WithExpected($"ETag={FormatETag(expectedETag)}")
                .WithObserved($"ETag={FormatETag(actual.ETag)}")
                .WithETags(actual.ETag, supplied: expectedETag)
                .WithSchedule(actual.StartAt, actual.Period)
                .Throw();
        }
    }

    /// <summary>
    /// Determines whether a reminder start time falls within the loading window which opens at <paramref name="now"/>.
    /// </summary>
    /// <param name="startAt">The reminder start time.</param>
    /// <param name="now">The window start.</param>
    /// <param name="window">The window length.</param>
    /// <returns><see langword="true"/> when the reminder is inside the window.</returns>
    protected static bool IsWithinWindow(DateTime startAt, DateTime now, TimeSpan window) => startAt <= now + window;

    /// <summary>
    /// Renders a reminder entry for diagnostics.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The rendered entry.</returns>
    protected static string Describe(ReminderEntry entry)
        => $"(GrainId={entry.GrainId}, ReminderName='{entry.ReminderName}', StartAt={entry.StartAt:O}, Period={entry.Period}, ETag={FormatETag(entry.ETag)})";

    /// <summary>
    /// Renders an ETag for diagnostics.
    /// </summary>
    /// <param name="etag">The ETag.</param>
    /// <returns>The rendered ETag.</returns>
    protected static string FormatETag(string? etag) => etag is null ? "<null>" : etag.Length == 0 ? "<empty>" : $"'{etag}'";

    private async Task<RangeFixture> CreateRangeFixtureAsync(string guarantee)
    {
        var items = new List<RangeItem>();
        var seen = new HashSet<uint>();

        for (var attempt = 0; attempt < 64 && items.Count < 3; attempt++)
        {
            var grainId = NewGrainId($"range-{attempt}");
            var hash = grainId.GetUniformHashCode();
            if (!seen.Add(hash))
            {
                continue;
            }

            var name = $"range-{items.Count}";
            var entry = NewEntry(grainId, name, BaseTime.AddMinutes(items.Count), TimeSpan.FromMinutes(items.Count + 1));
            var etag = await UpsertAsync(entry, guarantee);
            items.Add(new RangeItem(entry, hash, etag));
        }

        if (items.Count < 3)
        {
            Report(guarantee, "CreateRangeFixture")
                .WithExpected("three grains with distinct uniform hash codes")
                .WithObserved($"{items.Count} distinct hashes after 64 attempts")
                .Throw();
        }

        items.Sort((left, right) => left.Hash.CompareTo(right.Hash));
        return new RangeFixture(this, items);
    }

    private void AssertRange(
        string guarantee,
        string operation,
        uint begin,
        uint end,
        RangeFixture fixtureState,
        ReminderTableData rows,
        IReadOnlyList<RangeItem> expectedIncluded,
        IReadOnlyList<RangeItem> expectedExcluded)
    {
        var expectedSnapshots = expectedIncluded
            .Select(item => ReminderTableEntrySnapshot.Create(item.Entry, item.ETag, Capabilities.SupportsSubSecondPrecision))
            .ToList();
        AssertExactEntries(guarantee, operation, expectedSnapshots, rows.Reminders, begin, end, fixtureState);
        var returnedIdentities = rows.Reminders
            .Select(reminder => new ReminderTableEntryIdentity(reminder.GrainId, reminder.ReminderName))
            .ToList();

        foreach (var item in expectedIncluded)
        {
            if (!returnedIdentities.Contains(new ReminderTableEntryIdentity(item.GrainId, item.ReminderName)))
            {
                Report(guarantee, operation)
                    .WithIdentity(item.GrainId, item.ReminderName)
                    .WithExpected($"the reminder with hash {item.Hash.ToString(CultureInfo.InvariantCulture)} to be inside the range")
                    .WithObserved($"it was absent from {rows.Reminders.Count} returned reminders")
                    .WithRange(begin, end)
                    .WithETags(item.ETag)
                    .WithOwnership("fixture", fixtureState.All.Select(entry => entry.Hash))
                    .WithOwnership("returned", rows.Reminders.Select(reminder => reminder.GrainId.GetUniformHashCode()))
                    .Throw();
            }
        }

        foreach (var item in expectedExcluded)
        {
            if (returnedIdentities.Contains(new ReminderTableEntryIdentity(item.GrainId, item.ReminderName)))
            {
                Report(guarantee, operation)
                    .WithIdentity(item.GrainId, item.ReminderName)
                    .WithExpected($"the reminder with hash {item.Hash.ToString(CultureInfo.InvariantCulture)} to be outside the range")
                    .WithObserved("it was returned")
                    .WithRange(begin, end)
                    .WithETags(item.ETag)
                    .WithOwnership("fixture", fixtureState.All.Select(entry => entry.Hash))
                    .WithOwnership("returned", rows.Reminders.Select(reminder => reminder.GrainId.GetUniformHashCode()))
                    .Throw();
            }
        }
    }

    private void AssertExactEntries(
            string guarantee,
            string operation,
            IReadOnlyList<ReminderTableEntrySnapshot> expected,
            IEnumerable<ReminderEntry> actualEntries,
            uint? begin = null,
            uint? end = null,
            RangeFixture? fixtureState = null)
        {
            var actualEntryList = actualEntries.ToList();
            var actual = actualEntryList
                .Select(entry => ReminderTableEntrySnapshot.Observe(entry, Capabilities.SupportsSubSecondPrecision))
                .ToList();
            var difference = ReminderTableEntrySnapshotComparer.CompareExact(expected, actual);
            if (difference is null)
            {
                return;
            }

            var report = Report(guarantee, operation)
                .WithExpected($"exact identities and complete entries [{string.Join(", ", expected)}]")
                .WithObserved($"[{string.Join(", ", actual)}]")
                .WithDetail("differingField", difference.Field)
                .WithDetail("comparison", difference.ToString());
            var identity = difference.Expected?.Identity ?? difference.Actual?.Identity;
            if (identity is null)
            {
                var expectedIdentities = expected.Select(entry => entry.Identity).ToHashSet();
                var actualIdentities = actual.Select(entry => entry.Identity).ToHashSet();
                identity = actual.FirstOrDefault(entry => !expectedIdentities.Contains(entry.Identity)).Identity;
                if (identity.Value == default)
                {
                    identity = expected.FirstOrDefault(entry => !actualIdentities.Contains(entry.Identity)).Identity;
                }
            }

            if (identity is { } value)
            {
                report.WithIdentity(value.GrainId, value.ReminderName);
            }

            if (begin is { } rangeBegin && end is { } rangeEnd)
            {
                report.WithRange(rangeBegin, rangeEnd);
            }

            if (fixtureState is not null)
            {
                report.WithOwnership("fixture", fixtureState.All.Select(entry => entry.Hash))
                    .WithOwnership("returned", actualEntryList.Select(entry => entry.GrainId.GetUniformHashCode()));
            }

            report.Throw();
        }

    private async Task CompleteUpsertBatchAsync(
            List<(ReminderEntry Entry, Task<string?> Upsert)> pending,
            List<(ReminderEntry Entry, string ETag)> created,
            string guarantee)
        {
            var etags = await Task.WhenAll(pending.Select(item => item.Upsert));
            for (var index = 0; index < pending.Count; index++)
            {
                var entry = pending[index].Entry;
                var etag = etags[index];
                if (string.IsNullOrEmpty(etag))
                {
                    Report(guarantee, "UpsertRow")
                        .WithIdentity(entry.GrainId, entry.ReminderName)
                        .WithExpected("a non-empty ETag from every bounded-batch upsert")
                        .WithObserved($"UpsertRow returned {FormatETag(etag)}")
                        .WithSchedule(entry.StartAt, entry.Period)
                        .Throw();
                }

                created.Add((entry, etag!));
            }

            pending.Clear();
    }

    /// <summary>
    /// A reminder created by the hash-range fixture.
    /// </summary>
    private sealed class RangeItem(ReminderEntry entry, uint hash, string etag)
    {
        public ReminderEntry Entry { get; } = entry;

        public GrainId GrainId => Entry.GrainId;

        public string ReminderName => Entry.ReminderName;

        public uint Hash { get; } = hash;

        public string ETag { get; } = etag;

        public bool Removed { get; set; }
    }

    private sealed class RangeFixture(ReminderTableTestRunner runner, List<RangeItem> items)
    {
        public IReadOnlyList<RangeItem> All { get; } = items;

        public void MarkRemoved(RangeItem item) => item.Removed = true;

        public async Task CleanupAsync()
        {
            foreach (var item in All)
            {
                if (!item.Removed)
                {
                    await runner.RemoveAsync(item.GrainId, item.ReminderName, item.ETag);
                }
            }
        }
    }
}
