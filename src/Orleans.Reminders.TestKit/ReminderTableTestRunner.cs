using System;
using System.Collections.Generic;
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
/// This runner is deliberately framework neutral: no test attributes are applied and failures are reported by
/// throwing <see cref="ReminderConformanceException"/> carrying a structured <see cref="ReminderFailureReport"/>.
/// Derive from it in a provider suite, apply your test framework's attributes to overrides, and call the base
/// implementation, exactly as <c>Orleans.Persistence.TestKit.GrainStorageTestRunner</c> is consumed.
/// </remarks>
public abstract class ReminderTableTestRunner
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromMinutes(1);
    private int _grainCounter;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderTableTestRunner"/> class.
    /// </summary>
    /// <param name="reminderTable">The reminder table under test.</param>
    /// <param name="providerName">The provider name reported in conformance diagnostics.</param>
    /// <param name="seed">The deterministic seed used to generate reminder identities.</param>
    protected ReminderTableTestRunner(IReminderTable reminderTable, string providerName, int seed = 0)
    {
        ReminderTable = reminderTable ?? throw new ArgumentNullException(nameof(reminderTable));
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ProviderName = providerName;
        Seed = seed;
    }

    /// <summary>
    /// Gets the reminder table under test.
    /// </summary>
    public IReminderTable ReminderTable { get; }

    /// <summary>
    /// Gets the deterministic seed used to generate reminder identities.
    /// </summary>
    public int Seed { get; }

    /// <summary>
    /// Gets the provider name reported in failure messages.
    /// </summary>
    protected string ProviderName { get; }

    /// <summary>
    /// Gets a deterministic base time used by schedule-sensitive guarantees.
    /// </summary>
    /// <remarks>The value is truncated to whole seconds so it round-trips through every built-in provider.</remarks>
    protected virtual DateTime BaseTime { get; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Guarantee: <see cref="IReminderTable.StartAsync"/> is idempotent and leaves the table usable.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_StartAsync_IsIdempotent()
        => RunReminderTable_StartAsync_IsIdempotent(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_StartAsync_IsIdempotent()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_StartAsync_IsIdempotent(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_StartAsync_IsIdempotent);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMinutes(1));
        await ReminderTable.StartAsync(cancellation.Token);
        await ReminderTable.StartAsync(cancellation.Token);

        var grainId = NewGrainId("start-idempotent");
        var entry = NewEntry(grainId, "start-idempotent");
        var etag = await UpsertAsync(entry, Guarantee, cancellationToken);

        await RemoveAsync(grainId, entry.ReminderName, etag, cancellationToken);
    }

    /// <summary>
    /// Guarantee: after <see cref="IReminderTable.StopAsync"/> the table can be restarted and resumes serving reads.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_StopAsync_ThenRestart_ResumesService()
        => RunReminderTable_StopAsync_ThenRestart_ResumesService(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_StopAsync_ThenRestart_ResumesService()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_StopAsync_ThenRestart_ResumesService(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_StopAsync_ThenRestart_ResumesService);
        var grainId = NewGrainId("restart");
        var entry = NewEntry(grainId, "restart");
        var etag = await UpsertAsync(entry, Guarantee, cancellationToken);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMinutes(1));
        await ReminderTable.StopAsync(cancellation.Token);
        await ReminderTable.StartAsync(cancellation.Token);

        var reread = await ReadUntilAsync(
            () => ReminderTable.ReadRow(grainId, entry.ReminderName),
            value => value is not null && EntryMatches(entry, etag, value),
            Guarantee,
            "ReadRow",
            $"the restarted table to return {Describe(entry)} with ETag {FormatETag(etag)}",
            cancellationToken);
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
        await RemoveAsync(grainId, entry.ReminderName, reread!.ETag!, cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Upsert, point read, grain read, identity
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Guarantee: a successful upsert returns a non-empty ETag.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_UpsertRow_ReturnsNewNonEmptyETag()
        => RunReminderTable_UpsertRow_ReturnsNewNonEmptyETag(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_UpsertRow_ReturnsNewNonEmptyETag()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_UpsertRow_ReturnsNewNonEmptyETag(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_UpsertRow_ReturnsNewNonEmptyETag);

        var grainId = NewGrainId("upsert-etag");
        var first = await UpsertAsync(NewEntry(grainId, "upsert-etag"), Guarantee, cancellationToken);
        var second = await UpsertAsync(
            NewEntry(grainId, "upsert-etag", BaseTime.AddMinutes(5)),
            Guarantee,
            cancellationToken,
            first);

        await RemoveAsync(grainId, "upsert-etag", second, cancellationToken);
    }

    /// <summary>
    /// Guarantee: a point read returns the persisted identity, schedule and ETag of an upserted reminder.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_UpsertRow_PersistsScheduleForPointRead()
        => RunReminderTable_UpsertRow_PersistsScheduleForPointRead(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_UpsertRow_PersistsScheduleForPointRead()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_UpsertRow_PersistsScheduleForPointRead(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_UpsertRow_PersistsScheduleForPointRead);

        var grainId = NewGrainId("point-read");
        var entry = NewEntry(grainId, "foo/bar\\#b_a_z?", BaseTime.AddMinutes(7), TimeSpan.FromMinutes(3));
        var etag = await UpsertAsync(entry, Guarantee, cancellationToken);

        var read = await ReadRequiredAsync(grainId, entry.ReminderName, Guarantee, cancellationToken, entry, etag);
        AssertEntry(Guarantee, "ReadRow", entry, etag, read);

        await RemoveAsync(grainId, entry.ReminderName, etag, cancellationToken);
    }

    /// <summary>
    /// Guarantee: a point read of an unknown reminder returns <see langword="null"/> rather than a default entry.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_ReadRow_MissingReminder_ReturnsNull()
        => RunReminderTable_ReadRow_MissingReminder_ReturnsNull(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_ReadRow_MissingReminder_ReturnsNull()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_ReadRow_MissingReminder_ReturnsNull(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_ReadRow_MissingReminder_ReturnsNull);

        var grainId = NewGrainId("missing-point-read");
        var read = await ReminderTable.ReadRow(grainId, "never-registered", cancellationToken).WaitAsync(cancellationToken);
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
    public virtual Task ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders()
        => RunReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_ForGrain_ReturnsOnlyThatGrainsReminders);

        var target = NewGrainId("grain-read-target");
        var other = NewGrainId("grain-read-other");

        var first = NewEntry(target, "alpha", BaseTime, TimeSpan.FromMinutes(1));
        var second = NewEntry(target, "beta", BaseTime.AddMinutes(1), TimeSpan.FromMinutes(2));
        var otherEntry = NewEntry(other, "alpha", BaseTime, TimeSpan.FromMinutes(1));
        var firstETag = await UpsertAsync(first, Guarantee, cancellationToken);
        var secondETag = await UpsertAsync(second, Guarantee, cancellationToken);
        var otherETag = await UpsertAsync(otherEntry, Guarantee, cancellationToken);

        var rows = await ReadUntilAsync(
            () => ReminderTable.ReadRows(target),
            value => value is not null && value.Reminders.Count == 2,
            Guarantee,
            "ReadRows(GrainId)",
            "both reminders written for the target grain",
            cancellationToken);
        var requiredRows = RequireRows(Guarantee, "ReadRows(GrainId)", rows);
        AssertExactEntries(
            Guarantee,
            "ReadRows(GrainId)",
            [
                ReminderTableEntrySnapshot.Create(first, firstETag, supportsSubSecondPrecision: false),
                ReminderTableEntrySnapshot.Create(second, secondETag, supportsSubSecondPrecision: false)
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

        await RemoveAsync(target, "alpha", firstETag, cancellationToken);
        await RemoveAsync(target, "beta", secondETag, cancellationToken);
        await RemoveAsync(other, "alpha", otherETag, cancellationToken);
    }

    /// <summary>
    /// Guarantee: the grain-scoped read of a grain with no reminders returns an empty, non-null result.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty()
        => RunReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_ForUnknownGrain_ReturnsEmpty);

        var grainId = NewGrainId("grain-read-unknown");
        var rows = await ReadRowsUntilExactAsync(
            () => ReminderTable.ReadRows(grainId),
            [],
            Guarantee,
            "ReadRows(GrainId)",
            cancellationToken);
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
    public virtual Task ReminderTable_Identity_IsGrainIdAndReminderName()
        => RunReminderTable_Identity_IsGrainIdAndReminderName(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_Identity_IsGrainIdAndReminderName()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_Identity_IsGrainIdAndReminderName(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_Identity_IsGrainIdAndReminderName);

        var grainA = NewGrainId("identity-a");
        var grainB = NewGrainId("identity-b");

        var aFirst = NewEntry(grainA, "shared-name", BaseTime, TimeSpan.FromMinutes(1));
        var aSecond = NewEntry(grainA, "other-name", BaseTime.AddMinutes(2), TimeSpan.FromMinutes(2));
        var bFirst = NewEntry(grainB, "shared-name", BaseTime.AddMinutes(4), TimeSpan.FromMinutes(3));

        var aFirstETag = await UpsertAsync(aFirst, Guarantee, cancellationToken);
        var aSecondETag = await UpsertAsync(aSecond, Guarantee, cancellationToken);
        var bFirstETag = await UpsertAsync(bFirst, Guarantee, cancellationToken);

        AssertEntry(Guarantee, "ReadRow", aFirst, aFirstETag, await ReadRequiredAsync(grainA, "shared-name", Guarantee, cancellationToken, aFirst, aFirstETag));
        AssertEntry(Guarantee, "ReadRow", aSecond, aSecondETag, await ReadRequiredAsync(grainA, "other-name", Guarantee, cancellationToken, aSecond, aSecondETag));
        AssertEntry(Guarantee, "ReadRow", bFirst, bFirstETag, await ReadRequiredAsync(grainB, "shared-name", Guarantee, cancellationToken, bFirst, bFirstETag));

        // Removing one identity must not affect the two identities which share one component with it.
        if (!await ReminderTable.RemoveRow(grainA, "shared-name", aFirstETag, cancellationToken).WaitAsync(cancellationToken))
        {
            Report(Guarantee, "RemoveRow")
                .WithIdentity(grainA, "shared-name")
                .WithExpected("removal with the current ETag to succeed")
                .WithObserved("RemoveRow returned false")
                .WithETags(aFirstETag, supplied: aFirstETag)
                .Throw();
        }

        AssertEntry(Guarantee, "ReadRow", aSecond, aSecondETag, await ReadRequiredAsync(grainA, "other-name", Guarantee, cancellationToken, aSecond, aSecondETag));
        AssertEntry(Guarantee, "ReadRow", bFirst, bFirstETag, await ReadRequiredAsync(grainB, "shared-name", Guarantee, cancellationToken, bFirst, bFirstETag));

        await RemoveAsync(grainA, "other-name", aSecondETag, cancellationToken);
        await RemoveAsync(grainB, "shared-name", bFirstETag, cancellationToken);
    }

    /// <summary>
    /// Guarantee: reminder names containing path, escape, fragment, and query characters round-trip unchanged.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_Identity_WithSpecialCharacters_RoundTrips()
        => RunReminderTable_Identity_WithSpecialCharacters_RoundTrips(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_Identity_WithSpecialCharacters_RoundTrips()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_Identity_WithSpecialCharacters_RoundTrips(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_Identity_WithSpecialCharacters_RoundTrips);
        const string ReminderName = "foo/bar\\#b_a_z?";
        var grainId = NewGrainId("special-characters");
        var expected = NewEntry(grainId, ReminderName);
        var etag = await UpsertAsync(expected, Guarantee, cancellationToken);

        AssertEntry(Guarantee, "ReadRow", expected, etag, await ReadRequiredAsync(grainId, ReminderName, Guarantee, cancellationToken, expected, etag));
        await RemoveAsync(grainId, ReminderName, etag, cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------------------
    // ETag semantics
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Guarantee: every successful upsert replaces the ETag, and the point read observes the newest ETag.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_UpsertRow_ReplacesETagOnEachWrite()
        => RunReminderTable_UpsertRow_ReplacesETagOnEachWrite(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_UpsertRow_ReplacesETagOnEachWrite()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_UpsertRow_ReplacesETagOnEachWrite(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_UpsertRow_ReplacesETagOnEachWrite);
        var grainId = NewGrainId("etag-replace");
        var observed = new List<string>();
        var previous = (string?)null;

        for (var i = 0; i < 3; i++)
        {
            var entry = NewEntry(grainId, "etag-replace", BaseTime.AddMinutes(i), TimeSpan.FromMinutes(1 + i));
            var etag = await UpsertAsync(entry, Guarantee, cancellationToken, previous);
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

            var read = await ReadRequiredAsync(grainId, "etag-replace", Guarantee, cancellationToken, entry, etag);
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

        await RemoveAsync(grainId, "etag-replace", observed[^1], cancellationToken);
    }

    /// <summary>
    /// Guarantee: conditional removal with the current ETag removes the row.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_RemoveRow_WithCurrentETag_RemovesRow()
        => RunReminderTable_RemoveRow_WithCurrentETag_RemovesRow(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_RemoveRow_WithCurrentETag_RemovesRow()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_RemoveRow_WithCurrentETag_RemovesRow(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_RemoveRow_WithCurrentETag_RemovesRow);

        var grainId = NewGrainId("remove-current");
        var entry = NewEntry(grainId, "remove-current");
        var etag = await UpsertAsync(entry, Guarantee, cancellationToken);

        var removed = await ReminderTable.RemoveRow(grainId, entry.ReminderName, etag, cancellationToken).WaitAsync(cancellationToken);
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
            "null after successful removal",
            cancellationToken);
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
    public virtual Task ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow()
        => RunReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_RemoveRow_WithStaleETag_FailsAndRetainsRow);
        var grainId = NewGrainId("remove-stale");
        var staleETag = await UpsertAsync(
            NewEntry(grainId, "remove-stale", BaseTime, TimeSpan.FromMinutes(1)),
            Guarantee,
            cancellationToken);
        var updated = NewEntry(grainId, "remove-stale", BaseTime.AddMinutes(9), TimeSpan.FromMinutes(4));
        var currentETag = await UpsertAsync(updated, Guarantee, cancellationToken, staleETag);

        var removed = await ReminderTable.RemoveRow(grainId, "remove-stale", staleETag, cancellationToken).WaitAsync(cancellationToken);
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

        var read = await ReadRequiredAsync(grainId, "remove-stale", Guarantee, cancellationToken, updated, currentETag);
        AssertEntry(Guarantee, "ReadRow", updated, currentETag, read);

        await RemoveAsync(grainId, "remove-stale", currentETag, cancellationToken);
    }

    /// <summary>
    /// Guarantee: removal targeting an unknown reminder name returns <see langword="false"/> and removes nothing.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse()
        => RunReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_RemoveRow_WithUnknownReminderName_ReturnsFalse);

        var grainId = NewGrainId("remove-unknown");
        var entry = NewEntry(grainId, "present");
        var etag = await UpsertAsync(entry, Guarantee, cancellationToken);

        var removed = await ReminderTable.RemoveRow(grainId, "absent", etag, cancellationToken).WaitAsync(cancellationToken);
        if (removed)
        {
            Report(Guarantee, "RemoveRow")
                .WithIdentity(grainId, "absent")
                .WithExpected("false when the reminder name does not exist")
                .WithObserved("RemoveRow returned true")
                .WithETags(etag, supplied: etag)
                .Throw();
        }

        AssertEntry(Guarantee, "ReadRow", entry, etag, await ReadRequiredAsync(grainId, "present", Guarantee, cancellationToken, entry, etag));
        await RemoveAsync(grainId, "present", etag, cancellationToken);
    }

    /// <summary>
    /// Guarantee: a repeated removal of an already removed reminder returns <see langword="false"/>.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess()
        => RunReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_RemoveRow_Repeated_ReturnsFalseAfterFirstSuccess);

        var grainId = NewGrainId("remove-twice");
        var entry = NewEntry(grainId, "remove-twice");
        var etag = await UpsertAsync(entry, Guarantee, cancellationToken);

        var first = await ReminderTable.RemoveRow(grainId, entry.ReminderName, etag, cancellationToken).WaitAsync(cancellationToken);
        var second = await ReminderTable.RemoveRow(grainId, entry.ReminderName, etag, cancellationToken).WaitAsync(cancellationToken);

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

    // ---------------------------------------------------------------------------------------------------------
    // Schedule updates and window movement
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Guarantee: an upsert on an existing identity updates <see cref="ReminderEntry.StartAt"/> and
    /// <see cref="ReminderEntry.Period"/> in place rather than creating a second row.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_UpsertRow_UpdatesStartAtAndPeriod()
        => RunReminderTable_UpsertRow_UpdatesStartAtAndPeriod(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_UpsertRow_UpdatesStartAtAndPeriod()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_UpsertRow_UpdatesStartAtAndPeriod(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_UpsertRow_UpdatesStartAtAndPeriod);

        var grainId = NewGrainId("schedule-update");
        var originalETag = await UpsertAsync(
            NewEntry(grainId, "schedule-update", BaseTime, TimeSpan.FromMinutes(1)),
            Guarantee,
            cancellationToken);

        var updated = NewEntry(grainId, "schedule-update", BaseTime.AddHours(2), TimeSpan.FromMinutes(17));
        var updatedETag = await UpsertAsync(updated, Guarantee, cancellationToken, originalETag);

        AssertEntry(Guarantee, "ReadRow", updated, updatedETag, await ReadRequiredAsync(grainId, "schedule-update", Guarantee, cancellationToken, updated, updatedETag));

        var rows = await ReadRowsUntilExactAsync(
            () => ReminderTable.ReadRows(grainId),
            [ReminderTableEntrySnapshot.Create(updated, updatedETag, supportsSubSecondPrecision: false)],
            Guarantee,
            "ReadRows(GrainId)",
            cancellationToken);
        if (rows.Reminders.Count != 1)
        {
            Report(Guarantee, "ReadRows(GrainId)")
                .WithIdentity(grainId, "schedule-update")
                .WithExpected("an update to replace the existing row rather than add a new one")
                .WithObserved($"{rows.Reminders.Count} rows: {string.Join(", ", rows.Reminders.Select(Describe))}")
                .WithSchedule(updated.StartAt, updated.Period)
                .Throw();
        }

        await RemoveAsync(grainId, "schedule-update", updatedETag, cancellationToken);
    }

    /// <summary>
    /// Guarantee: moving a reminder's start time across a loading window boundary is observable through both the
    /// point read and the grain-scoped read, and the previous schedule is no longer visible.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows()
        => RunReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_UpsertRow_MovesReminderBetweenLoadingWindows);

        var window = TimeSpan.FromMinutes(10);
        var grainId = NewGrainId("window-move");

        var inside = NewEntry(grainId, "window-move", BaseTime.AddMinutes(2), TimeSpan.FromMinutes(30));
        var insideETag = await UpsertAsync(inside, Guarantee, cancellationToken);
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
        var outsideETag = await UpsertAsync(outside, Guarantee, cancellationToken, insideETag);

        var read = await ReadRequiredAsync(grainId, "window-move", Guarantee, cancellationToken, outside, outsideETag);
        AssertEntry(Guarantee, "ReadRow", outside, outsideETag, read);

        var expectedRows = new[]
        {
            ReminderTableEntrySnapshot.Create(outside, outsideETag, supportsSubSecondPrecision: false)
        };
        var rows = await ReadRowsUntilExactAsync(
            () => ReminderTable.ReadRows(0, 0),
            expectedRows,
            Guarantee,
            "ReadRows(0, 0)",
            cancellationToken);
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

        await RemoveAsync(grainId, "window-move", outsideETag, cancellationToken);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Hash range semantics
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Guarantee: the degenerate range <c>(0, 0]</c> enumerates every reminder in the table.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_ReadRows_FullRange_ReturnsAllReminders()
        => RunReminderTable_ReadRows_FullRange_ReturnsAllReminders(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_ReadRows_FullRange_ReturnsAllReminders()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_ReadRows_FullRange_ReturnsAllReminders(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_FullRange_ReturnsAllReminders);

        var fixtureState = await CreateRangeFixtureAsync(Guarantee, cancellationToken);
        try
        {
            var full = await ReadRangeUntilExactAsync(
                () => ReminderTable.ReadRows(0, 0),
                fixtureState.All,
                Guarantee,
                "ReadRows(0, 0)",
                cancellationToken);
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
    public virtual Task ReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering()
        => RunReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_UnsignedBoundary_UsesUInt32Ordering);
        var fixtureState = await CreateRangeFixtureAsync(Guarantee, cancellationToken);
        try
        {
            var expected = fixtureState.All.Where(item => item.Hash != 0).ToList();
            var excluded = fixtureState.All.Where(item => item.Hash == 0).ToList();
            var bounded = await ReadRangeUntilExactAsync(
                () => ReminderTable.ReadRows(0, uint.MaxValue),
                expected,
                Guarantee,
                "ReadRows(0, uint.MaxValue)",
                cancellationToken);
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
    public virtual Task ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality(int reminderCount)
        => RunReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality(reminderCount, CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality(int)"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality(
        int reminderCount,
        CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_FullRange_ReturnsExactRequestedCardinality);

        if (reminderCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reminderCount), reminderCount, "The requested reminder count must be positive.");
        }

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
                created.Add((entry, await UpsertAsync(entry, Guarantee, cancellationToken)));
            }

            var expected = created.Select(item => ReminderTableEntrySnapshot.Create(
                    item.Entry,
                    item.ETag,
                    supportsSubSecondPrecision: false)).ToList();
            var rows = await ReadRowsUntilExactAsync(
                () => ReminderTable.ReadRows(0, 0),
                expected,
                Guarantee,
                "ReadRows(0, 0)",
                cancellationToken);
            AssertExactEntries(Guarantee, "ReadRows(0, 0)", expected, rows.Reminders);
        }
        finally
        {
            using var cleanupCancellation = new CancellationTokenSource(CleanupTimeout);
            foreach (var item in created)
            {
                await RemoveAsync(
                    item.Entry.GrainId,
                    item.Entry.ReminderName,
                    item.ETag,
                    cleanupCancellation.Token);
            }
        }
    }

    /// <summary>
    /// Guarantee: a non-wrapping range is exclusive of <c>begin</c> and inclusive of <c>end</c>.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd()
        => RunReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_Range_ExcludesBeginAndIncludesEnd);
        var fixtureState = await CreateRangeFixtureAsync(Guarantee, cancellationToken);
        try
        {
            var low = fixtureState.All[0];
            var middle = fixtureState.All[1];
            var high = fixtureState.All[2];

            var rows = await ReadRangeUntilExactAsync(
                () => ReminderTable.ReadRows(low.Hash, middle.Hash),
                [middle],
                Guarantee,
                "ReadRows(low, middle)",
                cancellationToken);
            AssertRange(Guarantee, "ReadRows(low, middle)", low.Hash, middle.Hash, fixtureState, rows, [middle], [low, high]);

            var upper = await ReadRangeUntilExactAsync(
                () => ReminderTable.ReadRows(middle.Hash, high.Hash),
                [high],
                Guarantee,
                "ReadRows(middle, high)",
                cancellationToken);
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
    public virtual Task ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment()
        => RunReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_WrapAroundRange_ReturnsWrappedSegment);
        var fixtureState = await CreateRangeFixtureAsync(Guarantee, cancellationToken);
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
                "ReadRows(high, low)",
                cancellationToken);
            AssertRange(Guarantee, "ReadRows(high, low)", high.Hash, low.Hash, fixtureState, wrapped, [low], [middle, high]);

            // (middle, low] wraps as well and contains 'high' (above begin) and 'low' (at or below end).
            var wrappedUnion = await ReadRangeUntilExactAsync(
                () => ReminderTable.ReadRows(middle.Hash, low.Hash),
                [low, high],
                Guarantee,
                "ReadRows(middle, low)",
                cancellationToken);
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
    public virtual Task ReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder()
        => RunReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_OutsideRange_DoesNotDeleteReminder);
        var grainId = NewGrainId("outside-range");
        var expected = NewEntry(grainId, "outside-range");
        var etag = await UpsertAsync(expected, Guarantee, cancellationToken);
        var hash = grainId.GetUniformHashCode();
        var end = unchecked(hash + 1);

        var rows = RequireRows(
            Guarantee,
            "ReadRows(hash, hash + 1)",
            await ReminderTable.ReadRows(hash, end, cancellationToken).WaitAsync(cancellationToken));
        if (rows.Reminders.Any(reminder => reminder.GrainId.Equals(grainId) && string.Equals(reminder.ReminderName, expected.ReminderName, StringComparison.Ordinal)))
        {
            Report(Guarantee, "ReadRows(hash, hash + 1)")
                .WithIdentity(grainId, expected.ReminderName)
                .WithRange(hash, end)
                .WithExpected("the reminder at the exclusive lower bound to be absent")
                .WithObserved("the range read returned the reminder")
                .Throw();
        }

        AssertEntry(Guarantee, "ReadRow", expected, etag, await ReadRequiredAsync(grainId, expected.ReminderName, Guarantee, cancellationToken, expected, etag));
        await RemoveAsync(grainId, expected.ReminderName, etag, cancellationToken);
    }

    /// <summary>
    /// Guarantee: a removed reminder disappears from range enumeration while its siblings remain.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder()
        => RunReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_ReadRows_AfterRemoval_OmitsRemovedReminder);

        var fixtureState = await CreateRangeFixtureAsync(Guarantee, cancellationToken);
        try
        {
            var removed = fixtureState.All[1];
            if (!await ReminderTable.RemoveRow(removed.GrainId, removed.ReminderName, removed.ETag, cancellationToken).WaitAsync(cancellationToken))
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
                "ReadRows(0, 0)",
                cancellationToken);
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
    public virtual Task ReminderTable_ReadRow_AfterRemoval_ReturnsNull()
        => RunReminderTable_ReadRow_AfterRemoval_ReturnsNull(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_ReadRow_AfterRemoval_ReturnsNull()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_ReadRow_AfterRemoval_ReturnsNull(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_ReadRow_AfterRemoval_ReturnsNull);

        var grainId = NewGrainId("deletion-observation");
        var entry = NewEntry(grainId, "deletion-observation", BaseTime.AddMinutes(11), TimeSpan.FromMinutes(6));
        var etag = await UpsertAsync(entry, Guarantee, cancellationToken);
        var hash = grainId.GetUniformHashCode();

        // A range which cannot contain this reminder proves absence from a page is not deletion.
        var disjointBegin = unchecked(hash + 1);
        var disjointEnd = unchecked(hash + 2);
        var disjoint = RequireRows(
            Guarantee,
            "ReadRows(disjoint)",
            await ReminderTable.ReadRows(disjointBegin, disjointEnd, cancellationToken).WaitAsync(cancellationToken));
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
            "the row to remain durably present after an excluding range read",
            cancellationToken);
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

        await RemoveAsync(grainId, entry.ReminderName, etag, cancellationToken);

        var afterRemoval = await ReadUntilAsync(
            () => ReminderTable.ReadRow(grainId, entry.ReminderName),
            static value => value is null,
            Guarantee,
            "ReadRow",
            "null after successful removal",
            cancellationToken);
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
    public virtual Task ReminderTable_ConcurrentUpserts_ProduceDistinctETags()
        => RunReminderTable_ConcurrentUpserts_ProduceDistinctETags(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_ConcurrentUpserts_ProduceDistinctETags()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_ConcurrentUpserts_ProduceDistinctETags(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_ConcurrentUpserts_ProduceDistinctETags);
        const int Count = 5;
        var grainId = NewGrainId("concurrent-upsert");

        var writes = await Task.WhenAll(Enumerable.Range(0, Count).Select(async index =>
        {
            var entry = NewEntry(grainId, "concurrent-upsert", BaseTime.AddSeconds(index), TimeSpan.FromMinutes(1));
            var etag = await ReminderTableRetryPolicy.MutateUntilAsync(
                () => ReminderTable.UpsertRow(entry),
                etag => !string.IsNullOrEmpty(etag),
                ProviderName,
                Guarantee,
                "UpsertRow",
                $"a non-empty ETag for ({grainId}, '{entry.ReminderName}')",
                FormatETag,
                cancellationToken);
            return (Entry: entry, ETag: etag);
        })).WaitAsync(cancellationToken);

        var etags = writes.Select(write => write.ETag).ToArray();
        var distinct = etags.Where(etag => !string.IsNullOrEmpty(etag)).Distinct(StringComparer.Ordinal).Count();
        if (distinct != Count)
        {
            Report(Guarantee, "UpsertRow")
                .WithIdentity(grainId, "concurrent-upsert")
                .WithExpected($"{Count} distinct ETags from {Count} concurrent upserts")
                .WithObserved($"{distinct} distinct ETags: [{string.Join(", ", etags.Select(FormatETag))}]")
                .Throw();
        }

        var read = await ReadUntilAsync(
            () => ReminderTable.ReadRow(grainId, "concurrent-upsert"),
            value => value is not null && writes.Any(write => EntryMatches(write.Entry, write.ETag, value)),
            Guarantee,
            "ReadRow",
            $"one complete entry matching its returned ETag from [{string.Join(", ", writes.Select(write => $"{Describe(write.Entry)} => {FormatETag(write.ETag)}"))}]",
            cancellationToken);
        if (read is null)
        {
            Report(Guarantee, "ReadRow")
                .WithIdentity(grainId, "concurrent-upsert")
                .WithExpected("one complete entry matching the schedule associated with its returned ETag")
                .WithObserved("ReadRow returned null")
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
                "exactly one durable row for one reminder identity",
                cancellationToken));
        if (rows.Reminders.Count != 1)
        {
            Report(Guarantee, "ReadRows(GrainId)")
                .WithIdentity(grainId, "concurrent-upsert")
                .WithExpected("exactly one durable row for one reminder identity")
                .WithObserved($"{rows.Reminders.Count} rows: {string.Join(", ", rows.Reminders.Select(Describe))}")
                .Throw();
        }

        await RemoveAsync(grainId, "concurrent-upsert", read!.ETag!, cancellationToken);
    }

    /// <summary>
    /// Guarantee: concurrent replacement streams across distinct grains remain isolated: every grain observes
    /// exactly one row from its own ETag stream.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated()
        => RunReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_ParallelUpserts_AcrossGrains_RemainIsolated);
        const int GrainCount = 5;
        const int PerGrain = 5;
        var grains = Enumerable.Range(0, GrainCount).Select(index => NewGrainId($"parallel-{index}")).ToList();

        // Seed serially so this guarantee isolates parallel replacement streams from same-identity insert contention.
        foreach (var grainId in grains)
        {
            await UpsertAsync(
                NewEntry(grainId, "parallel", BaseTime, TimeSpan.FromMinutes(1)),
                Guarantee,
                cancellationToken);
        }

        var results = await Task.WhenAll(grains.Select(async grainId =>
        {
            var entries = Enumerable.Range(0, PerGrain)
                .Select(index => NewEntry(grainId, "parallel", BaseTime.AddSeconds(index + 1), TimeSpan.FromMinutes(index + 2)))
                .ToList();

            var etags = new string?[entries.Count];
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                etags[index] = await ReminderTableRetryPolicy.MutateUntilAsync(
                    () => ReminderTable.UpsertRow(entry),
                    etag => !string.IsNullOrEmpty(etag),
                    ProviderName,
                    Guarantee,
                    "UpsertRow",
                    $"a non-empty ETag within the replacement stream for grain {grainId}",
                    FormatETag,
                    cancellationToken);
            }

            return (GrainId: grainId, Entries: entries, ETags: etags);
        })).WaitAsync(cancellationToken);

        foreach (var (grainId, entries, etags) in results)
        {
            if (etags.Any(string.IsNullOrEmpty) || etags.Distinct(StringComparer.Ordinal).Count() != PerGrain)
            {
                Report(Guarantee, "UpsertRow")
                    .WithIdentity(grainId, "parallel")
                    .WithExpected($"{PerGrain} successful replacements with distinct non-empty ETags")
                    .WithObserved($"ETags: [{string.Join(", ", etags.Select(FormatETag))}]")
                    .Throw();
            }

            var rows = await ReadUntilAsync(
                () => ReminderTable.ReadRows(grainId),
                value => value is not null
                    && value.Reminders.Count == 1
                    && etags.Contains(value.Reminders[0].ETag, StringComparer.Ordinal),
                Guarantee,
                "ReadRows(GrainId)",
                $"one durable replacement for grain {grainId} with an ETag from its own stream",
                cancellationToken);
            var row = RequireRows(Guarantee, "ReadRows(GrainId)", rows).Reminders.Single();
            var expectedIndex = Array.FindIndex(etags, etag => string.Equals(etag, row.ETag, StringComparison.Ordinal));
            if (expectedIndex < 0 || !row.GrainId.Equals(grainId))
            {
                Report(Guarantee, "ReadRows(GrainId)")
                    .WithIdentity(grainId, "parallel")
                    .WithExpected($"one reminder owned by {grainId} with an ETag from its replacement stream")
                    .WithObserved(Describe(row))
                    .WithOwnership("grain", [grainId.GetUniformHashCode()])
                    .Throw();
            }

            AssertEntry(Guarantee, "ReadRows(GrainId)", entries[expectedIndex], etags[expectedIndex]!, row);
            await ReminderTable.RemoveRow(grainId, row.ReminderName, row.ETag!, cancellationToken).WaitAsync(cancellationToken);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Clear
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Guarantee: <see cref="IReminderTable.TestOnlyClearTable()"/> removes every reminder.
    /// </summary>
    /// <returns>A task which represents the asynchronous operation.</returns>
    public virtual Task ReminderTable_TestOnlyClearTable_RemovesAllReminders()
        => RunReminderTable_TestOnlyClearTable_RemovesAllReminders(CancellationToken.None);

    /// <inheritdoc cref="ReminderTable_TestOnlyClearTable_RemovesAllReminders()"/>
    /// <param name="cancellationToken">A cancellation token.</param>
    public virtual async Task RunReminderTable_TestOnlyClearTable_RemovesAllReminders(CancellationToken cancellationToken)
    {
        const string Guarantee = nameof(ReminderTable_TestOnlyClearTable_RemovesAllReminders);
        var first = NewGrainId("clear-a");
        var second = NewGrainId("clear-b");
        await UpsertAsync(NewEntry(first, "clear-a"), Guarantee, cancellationToken);
        await UpsertAsync(NewEntry(second, "clear-b"), Guarantee, cancellationToken);

        await ReminderTable.TestOnlyClearTable(cancellationToken).WaitAsync(cancellationToken);

        var rows = RequireRows(
            Guarantee,
            "ReadRows(0, 0)",
            await ReadUntilAsync(
                () => ReminderTable.ReadRows(0, 0),
                value => value is not null && value.Reminders.Count == 0,
                Guarantee,
                "ReadRows(0, 0)",
                "an empty table after TestOnlyClearTable",
                cancellationToken));
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
                $"null for ({grainId}, '{name}') after TestOnlyClearTable",
                cancellationToken);
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
    /// Creates a reminder entry with a whole-second UTC schedule supported by every built-in provider.
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
    /// Normalizes a timestamp to whole-second UTC precision supported by every built-in provider.
    /// </summary>
    /// <param name="value">The timestamp.</param>
    /// <returns>The normalized timestamp.</returns>
    protected DateTime Normalize(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    /// <summary>
    /// Upserts an entry and asserts that a non-empty ETag was returned.
    /// </summary>
    /// <param name="entry">The entry to upsert.</param>
    /// <param name="guarantee">The guarantee being verified.</param>
    /// <param name="previousETag">The ETag which a replacement must rotate, or <see langword="null"/> for a new row.</param>
    /// <returns>The new ETag.</returns>
    protected Task<string> UpsertAsync(ReminderEntry entry, string guarantee, string? previousETag = null)
        => UpsertAsync(entry, guarantee, CancellationToken.None, previousETag);

    /// <summary>
    /// Upserts an entry and asserts that a non-empty ETag was returned.
    /// </summary>
    /// <param name="entry">The entry to upsert.</param>
    /// <param name="guarantee">The guarantee being verified.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <param name="previousETag">The ETag which a replacement must rotate, or <see langword="null"/> for a new row.</param>
    /// <returns>The new ETag.</returns>
    protected async Task<string> UpsertAsync(
        ReminderEntry entry,
        string guarantee,
        CancellationToken cancellationToken,
        string? previousETag = null)
    {
        var etag = (await ReminderTableRetryPolicy.MutateUntilAsync(
            () => ReminderTable.UpsertRow(entry),
            etag => !string.IsNullOrEmpty(etag),
            ProviderName,
            guarantee,
            "UpsertRow",
            "a non-empty provider-issued ETag",
            FormatETag,
            cancellationToken))!;

        if (previousETag is not null && string.Equals(previousETag, etag, StringComparison.Ordinal))
        {
            Report(guarantee, "UpsertRow")
                .WithIdentity(entry.GrainId, entry.ReminderName)
                .WithExpected($"a replacement ETag different from {FormatETag(previousETag)}")
                .WithObserved($"the successful replacement returned the reused ETag {FormatETag(etag)}")
                .WithETags(etag, previousETag)
                .WithSchedule(entry.StartAt, entry.Period)
                .Throw();
        }

        return etag;
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
        => await ReadRequiredAsync(
            grainId,
            reminderName,
            guarantee,
            CancellationToken.None,
            expected,
            expectedETag);

    /// <summary>
    /// Reads a reminder which the guarantee requires to be present.
    /// </summary>
    /// <param name="grainId">The grain identifier.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="guarantee">The guarantee being verified.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <param name="expected">The expected entry.</param>
    /// <param name="expectedETag">The expected ETag.</param>
    /// <returns>The reminder entry.</returns>
    protected async Task<ReminderEntry> ReadRequiredAsync(
        GrainId grainId,
        string reminderName,
        string guarantee,
        CancellationToken cancellationToken,
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
                : $"{Describe(expected)} with ETag {FormatETag(expectedETag)}",
            cancellationToken);
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
        string expected,
        CancellationToken cancellationToken)
        => ReminderTableRetryPolicy.ReadUntilAsync(
            read,
            hasConverged,
            ProviderName,
            guarantee,
            operation,
            expected,
            value => value switch
            {
                null => "null",
                ReminderEntry entry => Describe(entry),
                ReminderTableData rows => $"{rows.Reminders.Count} rows: [{string.Join(", ", rows.Reminders.Select(Describe))}]",
                _ => value.ToString() ?? "<null>"
            },
            cancellationToken);

    private bool EntryMatches(ReminderEntry expected, string? expectedETag, ReminderEntry actual)
        => actual.GrainId.Equals(expected.GrainId)
            && string.Equals(actual.ReminderName, expected.ReminderName, StringComparison.Ordinal)
            && Normalize(actual.StartAt).Ticks == Normalize(expected.StartAt).Ticks
            && actual.Period == expected.Period
            && string.Equals(actual.ETag, expectedETag, StringComparison.Ordinal);

    /// <summary>
    /// Removes a reminder during test cleanup.
    /// </summary>
    /// <param name="grainId">The grain identifier.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="etag">The current ETag.</param>
    /// <returns>A task which represents the asynchronous operation.</returns>
    protected Task RemoveAsync(GrainId grainId, string reminderName, string etag)
        => RemoveAsync(grainId, reminderName, etag, CancellationToken.None);

    /// <summary>
    /// Removes a reminder during test cleanup.
    /// </summary>
    /// <param name="grainId">The grain identifier.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <param name="etag">The current ETag.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task which represents the asynchronous operation.</returns>
    protected async Task RemoveAsync(
        GrainId grainId,
        string reminderName,
        string etag,
        CancellationToken cancellationToken)
    {
        await ReminderTable.RemoveRow(grainId, reminderName, etag, cancellationToken).WaitAsync(cancellationToken);
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
        string operation,
        CancellationToken cancellationToken)
    {
        var rows = await ReminderTableRetryPolicy.ReadUntilAsync(
            read,
            value => value is not null
                && ReminderTableEntrySnapshotComparer.CompareExact(
                    expected,
                    value.Reminders.Select(entry => ReminderTableEntrySnapshot.Observe(entry, supportsSubSecondPrecision: false)).ToList()) is null,
            ProviderName,
            guarantee,
            operation,
            $"exact identities and complete entries [{string.Join(", ", expected)}]",
            value =>
            {
                if (value is null)
                {
                    return "null";
                }

                var actual = value.Reminders
                    .Select(entry => ReminderTableEntrySnapshot.Observe(entry, supportsSubSecondPrecision: false))
                    .ToList();
                var difference = ReminderTableEntrySnapshotComparer.CompareExact(expected, actual);
                return difference is null
                    ? $"[{string.Join(", ", actual)}]"
                    : $"differingField: '{difference.Field}'. {difference}. Expected=[{string.Join(", ", expected)}], Actual=[{string.Join(", ", actual)}]";
            },
            cancellationToken);
        return RequireRows(guarantee, operation, rows);
    }

    private Task<ReminderTableData> ReadRangeUntilExactAsync(
        Func<Task<ReminderTableData>> read,
        IReadOnlyList<RangeItem> expected,
        string guarantee,
        string operation,
        CancellationToken cancellationToken)
        => ReadRowsUntilExactAsync(
            read,
            expected.Select(item => ReminderTableEntrySnapshot.Create(
                item.Entry,
                item.ETag,
                supportsSubSecondPrecision: false)).ToList(),
            guarantee,
            operation,
            cancellationToken);

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
                .WithDetail("precision", "whole-second")
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

    private async Task<RangeFixture> CreateRangeFixtureAsync(
        string guarantee,
        CancellationToken cancellationToken)
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
            var etag = await UpsertAsync(entry, guarantee, cancellationToken);
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
            .Select(item => ReminderTableEntrySnapshot.Create(item.Entry, item.ETag, supportsSubSecondPrecision: false))
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
            .Select(entry => ReminderTableEntrySnapshot.Observe(entry, supportsSubSecondPrecision: false))
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

    private sealed class RangeFixture(
        ReminderTableTestRunner runner,
        List<RangeItem> items)
    {
        public IReadOnlyList<RangeItem> All { get; } = items;

        public void MarkRemoved(RangeItem item) => item.Removed = true;

        public async Task CleanupAsync()
        {
            using var cleanupCancellation = new CancellationTokenSource(CleanupTimeout);
            foreach (var item in All)
            {
                if (!item.Removed)
                {
                    await runner.RemoveAsync(
                        item.GrainId,
                        item.ReminderName,
                        item.ETag,
                        cleanupCancellation.Token);
                }
            }
        }
    }
}
