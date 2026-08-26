using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Accordant;
using Orleans.Runtime;

namespace Orleans.Reminders.TestKit;

/// <summary>
/// Configures the generated model-based conformance tests for an <see cref="IReminderTable"/> implementation.
/// </summary>
public sealed class ReminderTableModelBasedConformanceOptions
{
    /// <summary>
    /// Gets or sets the provider name reported in generated failures.
    /// </summary>
    public string ProviderName { get; set; } = "ReminderTable";

    /// <summary>
    /// Gets or sets the grain type used for generated reminder identities.
    /// </summary>
    public string GrainType { get; set; } = "reminder-testkit-model-grain";

    /// <summary>
    /// Gets or sets an optional fixed prefix for generated grain keys.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Gets or sets the maximum depth explored while generating operation sequences.
    /// </summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>
    /// Gets or sets the maximum number of operations in a generated sequence.
    /// </summary>
    public int MaxSequenceLength { get; set; } = 3;

    /// <summary>
    /// Gets or sets the deterministic seed used to generate reminder identities.
    /// </summary>
    public int Seed { get; set; }
}

/// <summary>
/// Generates sequences of reminder table operations and verifies an <see cref="IReminderTable"/> implementation
/// against a behavioral model.
/// </summary>
/// <remarks>
/// The modeled state tracks reminder identity, the persisted schedule, the current and previous ETags, existence and
/// hash ownership. Generated failures report the operation sequence, the provider, the inputs, the expected state and
/// the observed result.
/// </remarks>
public sealed class ReminderTableModelBasedTestRunner
{
    private readonly IReminderTable _reminderTable;
    private readonly ReminderTableModelBasedConformanceOptions _options;
    private readonly Action<string>? _output;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderTableModelBasedTestRunner"/> class.
    /// </summary>
    /// <param name="reminderTable">The reminder table to test.</param>
    /// <param name="providerName">The provider name reported in generated failures.</param>
    /// <param name="output">An optional callback which receives failure details.</param>
    public ReminderTableModelBasedTestRunner(IReminderTable reminderTable, string providerName, Action<string>? output = null)
        : this(
            reminderTable,
            new ReminderTableModelBasedConformanceOptions { ProviderName = providerName },
            output)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderTableModelBasedTestRunner"/> class.
    /// </summary>
    /// <param name="reminderTable">The reminder table to test.</param>
    /// <param name="options">The generated test configuration.</param>
    /// <param name="output">An optional callback which receives failure details.</param>
    public ReminderTableModelBasedTestRunner(IReminderTable reminderTable, ReminderTableModelBasedConformanceOptions options, Action<string>? output = null)
    {
        _reminderTable = reminderTable ?? throw new ArgumentNullException(nameof(reminderTable));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _output = output;
    }

    /// <summary>
    /// Generates and executes reminder table operation sequences.
    /// </summary>
    /// <returns>A task which represents the asynchronous test run.</returns>
    /// <exception cref="ReminderConformanceException">One or more generated test cases failed.</exception>
    public Task RunGeneratedConformanceTests() => RunGeneratedConformanceTests(CancellationToken.None);

    /// <summary>
    /// Generates and executes reminder table operation sequences.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task which represents the asynchronous test run.</returns>
    /// <exception cref="ReminderConformanceException">One or more generated test cases failed.</exception>
    public async Task RunGeneratedConformanceTests(CancellationToken cancellationToken)
    {
        var results = await ReminderTableModelBasedConformance.RunGeneratedTests(
            _reminderTable,
            _options,
            cancellationToken,
            _output);
        var failures = results
            .Where(result => !result.Success)
            .Select(result => ReminderTableModelBasedConformance.BuildFailureMessage(_options.ProviderName, _options.Seed, result))
            .ToList();

        if (failures.Count > 0)
        {
            throw new ReminderConformanceException(string.Join(Environment.NewLine, failures));
        }
    }
}

internal static class ReminderTableModelBasedConformance
{

    public static async Task<IList<TestCaseExecutionResult>> RunGeneratedTests(
        IReminderTable reminderTable,
        ReminderTableModelBasedConformanceOptions options,
        CancellationToken cancellationToken,
        Action<string>? output = null)
    {
        ArgumentNullException.ThrowIfNull(reminderTable);
        ArgumentNullException.ThrowIfNull(options);

        var runId = options.KeyPrefix ?? $"{Sanitize(options.ProviderName)}-{options.Seed.ToString("X8", CultureInfo.InvariantCulture)}";
        var spec = new ReminderTableBehavioralSpec();
        var initialState = new ReminderTableModelState();
        var inputSet = spec.CreateInputSet();
        var testCases = spec.GenerateTests(
            initialState,
            inputSet,
            new TestGenerationOptions
            {
                MaxDepth = options.MaxDepth,
                SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(options.MaxSequenceLength),
                ShouldApply = (input, state) => ReminderTableBehavioralSpec.CanApply((ReminderRequest)input.Request, (ReminderTableModelState)state)
            });

        var context = spec.CreateTestingContext();
        context.RequestPrinter = request => request?.ToString() ?? "<null>";
        context.ResponsePrinter = response => response?.ToString() ?? "<null>";

        var testIndex = 0;
        IList<TestCaseExecutionResult>? results = null;
        try
        {
            var executionResults = await spec.RunTests(
                context,
                initialState,
                testCases,
                new TestExecutionOptions
                {
                    StopOnFirstFailure = true,
                    BeforeEach = info =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        reminderTable.TestOnlyClearTable().WaitAsync(cancellationToken).GetAwaiter().GetResult();
                        var prefix = $"{runId}-{testIndex++:D4}";
                        info.Context.Register(new ReminderExecutionContext(
                            reminderTable,
                            options.ProviderName,
                            options.GrainType,
                            prefix,
                            options.Seed,
                            cancellationToken));
                    },
                    AfterEach = info =>
                    {
                        try
                        {
                            using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                            reminderTable.TestOnlyClearTable()
                                .WaitAsync(cleanupCancellation.Token)
                                .GetAwaiter()
                                .GetResult();
                        }
                        catch when (!info.Success || cancellationToken.IsCancellationRequested)
                        {
                            // Preserve the generated failure or test cancellation.
                        }

                        if (!info.Success)
                        {
                            output?.Invoke(info.FailureMessage);
                        }
                    }
                }).WaitAsync(cancellationToken);
            results = executionResults;

            return executionResults;
        }
        finally
        {
            try
            {
                using var cleanupCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await reminderTable.TestOnlyClearTable().WaitAsync(cleanupCancellation.Token);
                var finalRows = await ReminderTableRetryPolicy.ReadUntilAsync(
                    () => reminderTable.ReadRows(0, 0),
                    rows => rows is not null && rows.Reminders.Count == 0,
                    options.ProviderName,
                    nameof(ReminderTableModelBasedTestRunner),
                    "FinalCleanup/ReadRows(0, 0)",
                    "an empty reminder table after final cleanup",
                    rows => rows is null
                        ? "null"
                        : $"{rows.Reminders.Count.ToString(CultureInfo.InvariantCulture)} rows",
                    cleanupCancellation.Token);
                if (finalRows is null || finalRows.Reminders.Count != 0)
                {
                    throw new ReminderConformanceException(
                        $"Final reminder table cleanup left {finalRows?.Reminders.Count.ToString(CultureInfo.InvariantCulture) ?? "null"} rows; expected an empty table.");
                }
            }
            catch when (
                results is null
                || cancellationToken.IsCancellationRequested
                || results.Any(result => !result.Success))
            {
                // Preserve the generated failure, test cancellation, or execution exception.
            }
        }
    }

    public static string BuildFailureMessage(string providerName, int seed, TestCaseExecutionResult result)
    {
        var builder = new StringBuilder("Model-based reminder table conformance test failed [provider=")
            .Append(string.IsNullOrWhiteSpace(providerName) ? "<unnamed-provider>" : providerName)
            .Append(", seed=")
            .Append(seed.ToString(CultureInfo.InvariantCulture))
            .Append("].");

        if (!string.IsNullOrWhiteSpace(result.LastFailureMessage))
        {
            builder.Append(' ').Append(result.LastFailureMessage);
        }

        if (!string.IsNullOrWhiteSpace(result.LogFilePath))
        {
            builder.Append(" Log: ").Append(result.LogFilePath);
        }

        return builder.ToString();
    }

    private static string Sanitize(string providerName)
        => string.IsNullOrWhiteSpace(providerName)
            ? "ReminderTable"
            : new string(providerName.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());

    private sealed class ReminderTableBehavioralSpec : Spec<ReminderTableModelState>
    {
        public readonly UpsertOperation Upsert;
        public readonly ReadRowOperation ReadRow;
        public readonly ReadGrainRowsOperation ReadGrainRows;
        public readonly ReadRangeOperation ReadRange;
        public readonly RemoveOperation Remove;
        public readonly ClearOperation Clear;

        public ReminderTableBehavioralSpec()
        {
            Upsert = new UpsertOperation("Upsert");
            ReadRow = new ReadRowOperation();
            ReadGrainRows = new ReadGrainRowsOperation();
            ReadRange = new ReadRangeOperation();
            Remove = new RemoveOperation();
            Clear = new ClearOperation();
            Add(Upsert);
            Add(ReadRow);
            Add(ReadGrainRows);
            Add(ReadRange);
            Add(Remove);
            Add(Clear);
        }

        public InputSet CreateInputSet()
        {
            var result = new InputSet
            {
                Upsert.With(new ReminderRequest(ReminderOperation.Upsert, ReminderKey.First, ScheduleId.One), "Upsert first reminder with schedule 1"),
                Upsert.With(new ReminderRequest(ReminderOperation.Upsert, ReminderKey.First, ScheduleId.Two), "Upsert first reminder with schedule 2"),
                Upsert.With(new ReminderRequest(ReminderOperation.Upsert, ReminderKey.SameGrain, ScheduleId.One), "Upsert sibling reminder on the same grain"),
                Upsert.With(new ReminderRequest(ReminderOperation.Upsert, ReminderKey.OtherGrain, ScheduleId.Two), "Upsert reminder on a different grain"),
                ReadRow.With(new ReminderRequest(ReminderOperation.ReadRow, ReminderKey.First), "Point read first reminder"),
                ReadRow.With(new ReminderRequest(ReminderOperation.ReadRow, ReminderKey.OtherGrain), "Point read other grain reminder"),
                ReadGrainRows.With(new ReminderRequest(ReminderOperation.ReadGrainRows, ReminderKey.First), "Grain read for the first grain"),
                ReadGrainRows.With(new ReminderRequest(ReminderOperation.ReadGrainRows, ReminderKey.OtherGrain), "Grain read for the other grain"),
                ReadRange.With(new ReminderRequest(ReminderOperation.ReadRange, ReminderKey.First, range: RangeMode.Full), "Range read over the whole ring"),
                Remove.With(new ReminderRequest(ReminderOperation.Remove, ReminderKey.First, etagMode: ETagMode.Current), "Remove first reminder with the current ETag"),
                Remove.With(new ReminderRequest(ReminderOperation.Remove, ReminderKey.First, etagMode: ETagMode.Stale), "Remove first reminder with a stale ETag"),
                Remove.With(new ReminderRequest(ReminderOperation.Remove, ReminderKey.First, etagMode: ETagMode.Missing), "Remove a reminder which does not exist"),
                Remove.With(new ReminderRequest(ReminderOperation.Remove, ReminderKey.OtherGrain, etagMode: ETagMode.Current), "Remove other grain reminder with the current ETag"),
                Clear.With(new ReminderRequest(ReminderOperation.Clear, ReminderKey.First), "Clear the table")
            };

            result.Add(ReadRange.With(
                new ReminderRequest(ReminderOperation.ReadRange, ReminderKey.First, range: RangeMode.OtherGrainOnly),
                "Range read owning only the other grain"));
            result.Add(ReadRange.With(
                new ReminderRequest(ReminderOperation.ReadRange, ReminderKey.First, range: RangeMode.ExcludingOtherGrain),
                "Wrap-around range read excluding the other grain"));

            return result;
        }

        public static bool CanApply(ReminderRequest? request, ReminderTableModelState state)
        {
            if (request is null)
            {
                return false;
            }

            var hasRecord = state.Records.TryGetValue(request.Key, out var record) && record is { Exists: true };
            return request.Operation switch
            {
                ReminderOperation.Upsert => true,
                ReminderOperation.ReadRow => true,
                ReminderOperation.ReadGrainRows => true,
                ReminderOperation.ReadRange => true,
                ReminderOperation.Remove => request.ETagMode switch
                {
                    ETagMode.Current => hasRecord,
                    ETagMode.Stale => hasRecord
                        && record is not null
                        && !string.IsNullOrEmpty(record.PreviousETag)
                        && !string.Equals(record.PreviousETag, record.ETag, StringComparison.Ordinal),
                    ETagMode.Missing => !hasRecord
                        && state.Records.Values.Any(entry => entry.Exists && !string.IsNullOrEmpty(entry.ETag)),
                    _ => false
                },
                ReminderOperation.Clear => state.Records.Values.Any(entry => entry.Exists),
                _ => false
            };
        }
    }

    private sealed class UpsertOperation : Operation<ReminderRequest, ReminderOperationResult, ReminderTableModelState>
    {
        public UpsertOperation(string name) : base(name) { }

        public override ExpectedOutcomes Apply(ReminderRequest request, ReminderTableModelState state)
        {
            state.Records.TryGetValue(request.Key, out var record);
            var previousETag = record is { Exists: true } ? record.ETag : null;
            var version = (record?.Version ?? 0) + 1;

            return Expect.That(result => ValidateUpsert(request, previousETag, version, result))
                .ThenState(
                    (result, nextState) =>
                    {
                        nextState.Records[request.Key] = CreateRecord(request, result.ETag, previousETag, version);
                    },
                    () => ReminderOperationResult.Upserted(request, DeterministicETag(request, version)));
        }

        public override Task<ReminderOperationResult> ExecuteAsync(TestingContext context, ReminderRequest request)
            => context.Get<ReminderExecutionContext>().UpsertAsync(request);
    }

    private sealed class ReadRowOperation : Operation<ReminderRequest, ReminderOperationResult, ReminderTableModelState>
    {
        public ReadRowOperation() : base("ReadRow")
        {
        }

        public override ExpectedOutcomes Apply(ReminderRequest request, ReminderTableModelState state)
        {
            state.Records.TryGetValue(request.Key, out var record);
            return Expect.That(result => ValidateReadRow(request, record, result)).SameState();
        }

        public override Task<ReminderOperationResult> ExecuteAsync(TestingContext context, ReminderRequest request)
            => context.Get<ReminderExecutionContext>().ReadRowAsync(request);
    }

    private sealed class ReadGrainRowsOperation : Operation<ReminderRequest, ReminderOperationResult, ReminderTableModelState>
    {
        public ReadGrainRowsOperation() : base("ReadGrainRows")
        {
        }

        public override ExpectedOutcomes Apply(ReminderRequest request, ReminderTableModelState state)
        {
            var expected = ExpectedRecords(state, key => ReminderKey.GrainOf(key) == ReminderKey.GrainOf(request.Key));
            return Expect.That(result => ValidateEntries(request, "ReadRows(GrainId)", expected, result)).SameState();
        }

        public override Task<ReminderOperationResult> ExecuteAsync(TestingContext context, ReminderRequest request)
            => context.Get<ReminderExecutionContext>().ReadGrainRowsAsync(request);
    }

    private sealed class ReadRangeOperation : Operation<ReminderRequest, ReminderOperationResult, ReminderTableModelState>
    {
        public ReadRangeOperation() : base("ReadRange")
        {
        }

        public override ExpectedOutcomes Apply(ReminderRequest request, ReminderTableModelState state)
        {
            var expected = request.Range switch
            {
                RangeMode.Full => ExpectedRecords(state, _ => true),
                RangeMode.OtherGrainOnly => ExpectedRecords(state, key => ReminderKey.GrainOf(key) == ReminderKey.OtherGrainName),
                RangeMode.ExcludingOtherGrain => ExpectedRecords(state, key => ReminderKey.GrainOf(key) != ReminderKey.OtherGrainName),
                _ => ExpectedRecords(state, _ => true)
            };

            return Expect.That(result => ValidateEntries(request, $"ReadRows({request.Range})", expected, result)).SameState();
        }

        public override Task<ReminderOperationResult> ExecuteAsync(TestingContext context, ReminderRequest request)
            => context.Get<ReminderExecutionContext>().ReadRangeAsync(request);
    }

    private sealed class RemoveOperation : Operation<ReminderRequest, ReminderOperationResult, ReminderTableModelState>
    {
        public RemoveOperation() : base("Remove")
        {
        }

        public override ExpectedOutcomes Apply(ReminderRequest request, ReminderTableModelState state)
        {
            state.Records.TryGetValue(request.Key, out var record);
            if (request.ETagMode == ETagMode.Current)
            {
                return Expect.That(result => ValidateRemove(request, expectedRemoved: true, record, result))
                    .ThenState(nextState => nextState.Records.Remove(request.Key));
            }

            return Expect.That(result => ValidateRemove(request, expectedRemoved: false, record, result)).SameState();
        }

        public override Task<ReminderOperationResult> ExecuteAsync(TestingContext context, ReminderRequest request)
            => context.Get<ReminderExecutionContext>().RemoveAsync(request);
    }

    private sealed class ClearOperation : Operation<ReminderRequest, ReminderOperationResult, ReminderTableModelState>
    {
        public ClearOperation() : base("Clear")
        {
        }

        public override ExpectedOutcomes Apply(ReminderRequest request, ReminderTableModelState state)
            => Expect.That(ValidateClear).ThenState(nextState => nextState.Records.Clear());

        public override Task<ReminderOperationResult> ExecuteAsync(TestingContext context, ReminderRequest request)
            => context.Get<ReminderExecutionContext>().ClearAsync(request);
    }

    private static List<ReminderModelRecord> ExpectedRecords(ReminderTableModelState state, Func<string, bool> predicate)
        => state.Records
            .Where(pair => pair.Value.Exists && predicate(pair.Key))
            .Select(pair => pair.Value)
            .OrderBy(record => record.LogicalKey, StringComparer.Ordinal)
            .ToList();

    private static ValidationResult ValidateUpsert(
        ReminderRequest request,
        string? previousETag,
        int version,
        ReminderOperationResult result)
    {
        if (!result.Succeeded)
        {
            return ValidationResult.Invalid(Describe(request, $"Upsert failed with {result.ExceptionType}"));
        }

        if (string.IsNullOrEmpty(result.ETag))
        {
            return ValidationResult.Invalid(Describe(request, $"Upsert returned no ETag; expected a non-empty ETag. observed={result}"));
        }

        if (previousETag is not null
            && string.Equals(previousETag, result.ETag, StringComparison.Ordinal))
        {
            return ValidationResult.Invalid(Describe(request, $"Upsert reused the previous ETag '{previousETag}'; expected a replacement ETag. observed={result}"));
        }

        if (result.Entries.Count == 1
            && !string.Equals(result.Entries[0].Snapshot.ETag, result.ETag, StringComparison.Ordinal))
        {
            return ValidationResult.Invalid(Describe(
                request,
                $"The point read observed ETag '{result.Entries[0].Snapshot.ETag}'; expected the upsert ETag '{result.ETag}'. observed={result}"));
        }

        var expected = CreateRecord(request, result.ETag, previousETag, version);
        return ValidateEntries(request, "UpsertRow/ReadRow", [expected], result);
    }

    private static ValidationResult ValidateReadRow(ReminderRequest request, ReminderModelRecord? record, ReminderOperationResult result)
    {
        if (!result.Succeeded)
        {
            return ValidationResult.Invalid(Describe(request, $"ReadRow failed with {result.ExceptionType}"));
        }

        if (record is not { Exists: true })
        {
            return result.Entries.Count != 0
                ? ValidationResult.Invalid(Describe(request, $"ReadRow returned an entry for a reminder which does not exist. observed={result}"))
                : ValidationResult.Valid();
        }

        return ValidateEntries(request, "ReadRow", [record], result);
    }

    private static ValidationResult ValidateEntries(
        ReminderRequest request,
        string operation,
        IReadOnlyList<ReminderModelRecord> expected,
        ReminderOperationResult result)
    {
        if (!result.Succeeded)
        {
            return ValidationResult.Invalid(Describe(request, $"{operation} failed with {result.ExceptionType}"));
        }

        var seenIdentities = new HashSet<ReminderTableEntryIdentity>();
        var seenLogicalKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in result.Entries)
        {
            if (!seenIdentities.Add(observation.Snapshot.Identity))
            {
                return ValidationResult.Invalid(Describe(
                    request,
                    $"{operation} returned duplicate identity {observation.Snapshot.Identity}. expected=[{string.Join(", ", expected.Select(Describe))}] observed={result}"));
            }

            if (observation.LogicalKey is null)
            {
                return ValidationResult.Invalid(Describe(
                    request,
                    $"{operation} returned unknown identity {observation.Snapshot.Identity}. expected=[{string.Join(", ", expected.Select(Describe))}] observed={result}"));
            }

            if (!seenLogicalKeys.Add(observation.LogicalKey))
            {
                return ValidationResult.Invalid(Describe(
                    request,
                    $"{operation} returned logical identity '{observation.LogicalKey}' more than once. observed={result}"));
            }
        }

        if (result.Entries.Count != expected.Count)
        {
            return ValidationResult.Invalid(Describe(
                request,
                $"{operation} returned {result.Entries.Count.ToString(CultureInfo.InvariantCulture)} entries; expected {expected.Count.ToString(CultureInfo.InvariantCulture)}. expected=[{string.Join(", ", expected.Select(Describe))}] observed={result}"));
        }

        var expectedByKey = expected.ToDictionary(record => record.LogicalKey, StringComparer.Ordinal);
        foreach (var observation in result.Entries)
        {
            if (!expectedByKey.TryGetValue(observation.LogicalKey!, out var expectedRecord))
            {
                return ValidationResult.Invalid(Describe(
                    request,
                    $"{operation} returned unexpected logical identity '{observation.LogicalKey}'. expected=[{string.Join(", ", expected.Select(Describe))}] observed={result}"));
            }

            var fieldDifference = Compare(expectedRecord, observation);
            if (fieldDifference is not null)
            {
                return ValidationResult.Invalid(Describe(
                    request,
                    $"{operation} entry field '{fieldDifference}' differs for logical identity '{expectedRecord.LogicalKey}'. expected={Describe(expectedRecord)} actual={observation} observed={result}"));
            }

            expectedByKey.Remove(observation.LogicalKey!);
        }

        return expectedByKey.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid(Describe(
                request,
                $"{operation} omitted logical identities [{string.Join(", ", expectedByKey.Keys)}]. observed={result}"));
    }

    private static ValidationResult ValidateRemove(ReminderRequest request, bool expectedRemoved, ReminderModelRecord? record, ReminderOperationResult result)
    {
        if (!result.Succeeded)
        {
            return ValidationResult.Invalid(Describe(request, $"RemoveRow failed with {result.ExceptionType}"));
        }

        if (result.Removed != expectedRemoved)
        {
            return ValidationResult.Invalid(Describe(
                request,
                $"RemoveRow returned {result.Removed.ToString(CultureInfo.InvariantCulture)}; expected {expectedRemoved.ToString(CultureInfo.InvariantCulture)}. observed={result}"));
        }

        if (expectedRemoved)
        {
            return result.Entries.Count != 0
                ? ValidationResult.Invalid(Describe(request, $"The reminder is still readable after a successful removal. observed={result}"))
                : ValidationResult.Valid();
        }

        if (record is not { Exists: true })
        {
            return result.Entries.Count != 0
                ? ValidationResult.Invalid(Describe(request, $"A failed removal produced a readable reminder. observed={result}"))
                : ValidationResult.Valid();
        }

        return ValidateEntries(request, "RemoveRow/ReadRow", [record], result);
    }

    private static ValidationResult ValidateClear(ReminderOperationResult result)
    {
        if (!result.Succeeded)
        {
            return ValidationResult.Invalid($"TestOnlyClearTable failed with {result.ExceptionType}");
        }

        return result.Entries.Count == 0
            ? ValidationResult.Valid()
            : ValidationResult.Invalid($"TestOnlyClearTable left [{string.Join(", ", result.Entries)}] behind; expected an empty table. observed={result}");
    }

    private static string Describe(ReminderRequest request, string message) => $"{message} request={request}";

    private static ReminderModelRecord CreateRecord(ReminderRequest request, string? etag, string? previousETag, int version)
        => new()
        {
            LogicalKey = request.Key,
            GrainIdentity = ReminderKey.GrainOf(request.Key),
            ReminderName = ReminderKey.ReminderNameOf(request.Key),
            StartAtTicks = ReminderExecutionContext.StartAtFor(request.Schedule).Ticks,
            PeriodTicks = ReminderExecutionContext.PeriodFor(request.Schedule).Ticks,
            ETag = etag,
            PreviousETag = previousETag,
            Exists = true,
            Version = version
        };

    private static string DeterministicETag(ReminderRequest request, int version)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"model-etag-{request.Key}-v{version}-s{request.Schedule}");

    private static string? Compare(ReminderModelRecord expected, ReminderObservedEntry actual)
    {
        if (!string.Equals(expected.LogicalKey, actual.LogicalKey, StringComparison.Ordinal))
        {
            return nameof(ReminderModelRecord.LogicalKey);
        }

        if (!string.Equals(expected.GrainIdentity, ReminderKey.GrainOf(actual.LogicalKey!), StringComparison.Ordinal))
        {
            return nameof(ReminderModelRecord.GrainIdentity);
        }

        if (!string.Equals(expected.ReminderName, actual.Snapshot.ReminderName, StringComparison.Ordinal))
        {
            return nameof(ReminderModelRecord.ReminderName);
        }

        if (expected.StartAtTicks != actual.Snapshot.StartAtTicks)
        {
            return nameof(ReminderModelRecord.StartAtTicks);
        }

        if (expected.PeriodTicks != actual.Snapshot.PeriodTicks)
        {
            return nameof(ReminderModelRecord.PeriodTicks);
        }

        return string.Equals(expected.ETag, actual.Snapshot.ETag, StringComparison.Ordinal)
            ? null
            : nameof(ReminderModelRecord.ETag);
    }

    private static string Describe(ReminderModelRecord record)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{{ LogicalKey='{record.LogicalKey}', GrainIdentity='{record.GrainIdentity}', ReminderName='{record.ReminderName}', StartAtTicks={record.StartAtTicks}, PeriodTicks={record.PeriodTicks}, ETag='{record.ETag}', PreviousETag='{record.PreviousETag}', Exists={record.Exists}, Version={record.Version} }}");

    private sealed class ReminderExecutionContext
    {
        private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly IReminderTable _reminderTable;
        private readonly string _providerName;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<string, (GrainId GrainId, string ReminderName)> _identities;
        private readonly Dictionary<string, string?> _currentETags = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _previousETags = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ReminderTableEntrySnapshot> _currentEntries = new(StringComparer.Ordinal);
        private readonly GrainId _otherGrain;

        public ReminderExecutionContext(
            IReminderTable reminderTable,
            string providerName,
            string grainType,
            string prefix,
            int seed,
            CancellationToken cancellationToken)
        {
            _reminderTable = reminderTable;
            _providerName = providerName;
            _cancellationToken = cancellationToken;
            var grainType1 = GrainType.Create(grainType);
            var primaryKey = $"{prefix}/{ReminderKey.PrimaryGrainName}";
            var primary = GrainId.Create(grainType1, GrainIdKeyExtensions.CreateGuidKey(ReminderTestData.CreateGuid(seed, primaryKey), primaryKey));
            GrainId other;
            var attempt = 0;
            do
            {
                var otherKey = $"{prefix}/{ReminderKey.OtherGrainName}/{attempt.ToString(CultureInfo.InvariantCulture)}";
                other = GrainId.Create(grainType1, GrainIdKeyExtensions.CreateGuidKey(ReminderTestData.CreateGuid(seed, otherKey), otherKey));
                attempt++;
            }
            while (other.GetUniformHashCode() == primary.GetUniformHashCode() && attempt < 64);

            _otherGrain = other;
            _identities = new Dictionary<string, (GrainId, string)>(StringComparer.Ordinal)
            {
                [ReminderKey.First] = (primary, "reminder-1"),
                [ReminderKey.SameGrain] = (primary, "reminder-2"),
                [ReminderKey.OtherGrain] = (other, "reminder-1")
            };
        }

        public async Task<ReminderOperationResult> UpsertAsync(ReminderRequest request)
        {
            var (grainId, reminderName) = _identities[request.Key];
            var entry = CreateEntry(grainId, reminderName, request.Schedule);
            try
            {
                var previous = _currentETags.TryGetValue(request.Key, out var current) ? current : null;
                var etag = await ReminderTableRetryPolicy.MutateUntilAsync(
                    () => _reminderTable.UpsertRow(entry),
                    value => !string.IsNullOrEmpty(value),
                    _providerName,
                    nameof(ReminderTableModelBasedTestRunner),
                    "UpsertRow",
                    $"a non-empty ETag for '{request.Key}'",
                    value => value ?? "<null>",
                    _cancellationToken);
                _previousETags[request.Key] = previous;
                _currentETags[request.Key] = etag;
                if (!string.IsNullOrEmpty(etag))
                {
                    _currentEntries[request.Key] = ReminderTableEntrySnapshot.Create(
                        entry,
                        etag,
                        supportsSubSecondPrecision: true);
                }

                var readBack = await ReadUntilAsync(
                    () => _reminderTable.ReadRow(grainId, reminderName),
                    value => string.IsNullOrEmpty(etag)
                        || value is not null && Matches(_currentEntries[request.Key], value),
                    "UpsertRow/ReadRow",
                    $"the upserted row '{request.Key}'");
                return ReminderOperationResult.Success(
                    etag: etag,
                    removed: false,
                    entries: ToObservedEntries(readBack is null ? [] : [readBack]));
            }
            catch (Exception exception) when (!_cancellationToken.IsCancellationRequested)
            {
                return ReminderOperationResult.Failure(exception);
            }
        }

        public async Task<ReminderOperationResult> ReadRowAsync(ReminderRequest request)
        {
            var (grainId, reminderName) = _identities[request.Key];
            try
            {
                ReminderTableEntrySnapshot? expected = _currentEntries.TryGetValue(request.Key, out var current)
                    ? current
                    : null;
                var entry = await ReadUntilAsync(
                    () => _reminderTable.ReadRow(grainId, reminderName),
                    value => expected is null ? value is null : value is not null && Matches(expected.Value, value),
                    "ReadRow",
                    expected is null ? $"no row for '{request.Key}'" : $"the current row '{request.Key}'");
                return ReminderOperationResult.Success(
                    etag: entry?.ETag,
                    removed: false,
                    entries: ToObservedEntries(entry is null ? [] : [entry]));
            }
            catch (Exception exception) when (!_cancellationToken.IsCancellationRequested)
            {
                return ReminderOperationResult.Failure(exception);
            }
        }

        public async Task<ReminderOperationResult> ReadGrainRowsAsync(ReminderRequest request)
        {
            var (grainId, _) = _identities[request.Key];
            try
            {
                var expected = _currentEntries
                    .Where(pair => _identities[pair.Key].GrainId.Equals(grainId))
                    .Select(pair => pair.Value)
                    .ToList();
                var rows = await ReadUntilAsync(
                    () => _reminderTable.ReadRows(grainId),
                    value => value is not null && Matches(expected, value.Reminders),
                    "ReadRows(GrainId)",
                    $"{expected.Count} current rows for grain '{request.Key}'");
                return ReminderOperationResult.Success(null, false, ToObservedEntries(rows?.Reminders ?? []));
            }
            catch (Exception exception) when (!_cancellationToken.IsCancellationRequested)
            {
                return ReminderOperationResult.Failure(exception);
            }
        }

        public async Task<ReminderOperationResult> ReadRangeAsync(ReminderRequest request)
        {
            var otherHash = _otherGrain.GetUniformHashCode();
            var (begin, end) = request.Range switch
            {
                RangeMode.OtherGrainOnly => (unchecked(otherHash - 1), otherHash),
                RangeMode.ExcludingOtherGrain => (otherHash, unchecked(otherHash - 1)),
                _ => (0u, 0u)
            };

            try
            {
                var expected = _currentEntries.Values
                    .Where(entry => IdealizedReminderTable.InRange(entry.GrainId.GetUniformHashCode(), begin, end))
                    .ToList();
                var rows = await ReadUntilAsync(
                    () => _reminderTable.ReadRows(begin, end),
                    value => value is not null && Matches(expected, value.Reminders),
                    $"ReadRows({request.Range})",
                    $"{expected.Count} current rows in ({begin}, {end}]");
                return ReminderOperationResult.Success(null, false, ToObservedEntries(rows?.Reminders ?? []));
            }
            catch (Exception exception) when (!_cancellationToken.IsCancellationRequested)
            {
                return ReminderOperationResult.Failure(exception);
            }
        }

        public async Task<ReminderOperationResult> RemoveAsync(ReminderRequest request)
        {
            var (grainId, reminderName) = _identities[request.Key];
            var etag = ResolveETag(request);
            if (string.IsNullOrEmpty(etag))
            {
                return ReminderOperationResult.Failure(
                    new InvalidOperationException($"No syntactically valid ETag is available for {request}."));
            }

            try
            {
                var removed = await _reminderTable.RemoveRow(grainId, reminderName, etag).WaitAsync(_cancellationToken);
                if (removed)
                {
                    _previousETags[request.Key] = null;
                    _currentETags[request.Key] = null;
                    _currentEntries.Remove(request.Key);

                    var expectedRemaining = _currentEntries.Values.ToList();
                    await ReadUntilAsync(
                        () => _reminderTable.ReadRows(0, 0),
                        value => value is not null && Matches(expectedRemaining, value.Reminders),
                        "RemoveRow/ReadRows(0, 0)",
                        $"{expectedRemaining.Count} unchanged rows after removing '{request.Key}'");
                    return ReminderOperationResult.Success(null, true, []);
                }

                ReminderTableEntrySnapshot? expected = _currentEntries.TryGetValue(request.Key, out var current)
                    ? current
                    : null;
                var readBack = await ReadUntilAsync(
                    () => _reminderTable.ReadRow(grainId, reminderName),
                    value => expected is null ? value is null : value is not null && Matches(expected.Value, value),
                    "RemoveRow/ReadRow",
                    removed ? $"no row for '{request.Key}'" : $"the unchanged row '{request.Key}'");
                return ReminderOperationResult.Success(
                    readBack?.ETag,
                    false,
                    ToObservedEntries(readBack is null ? [] : [readBack]));
            }
            catch (Exception exception) when (!_cancellationToken.IsCancellationRequested)
            {
                return ReminderOperationResult.Failure(exception);
            }
        }

        public async Task<ReminderOperationResult> ClearAsync(ReminderRequest request)
        {
            _ = request;
            try
            {
                await _reminderTable.TestOnlyClearTable().WaitAsync(_cancellationToken);
                _currentETags.Clear();
                _previousETags.Clear();
                _currentEntries.Clear();

                var rows = await ReadUntilAsync(
                    () => _reminderTable.ReadRows(0, 0),
                    value => value is not null && value.Reminders.Count == 0,
                    "TestOnlyClearTable/ReadRows",
                    "an empty reminder table");
                return ReminderOperationResult.Success(null, false, ToObservedEntries(rows?.Reminders ?? []));
            }
            catch (Exception exception) when (!_cancellationToken.IsCancellationRequested)
            {
                return ReminderOperationResult.Failure(exception);
            }
        }

        private string? ResolveETag(ReminderRequest request) => request.ETagMode switch
        {
            ETagMode.Current => _currentETags.TryGetValue(request.Key, out var current) ? current : null,
            ETagMode.Stale => _previousETags.TryGetValue(request.Key, out var previous) ? previous : null,
            ETagMode.Missing => _currentETags
                .Where(pair => !string.Equals(pair.Key, request.Key, StringComparison.Ordinal))
                .Select(pair => pair.Value)
                .FirstOrDefault(value => !string.IsNullOrEmpty(value)),
            _ => null
        };

        private Task<T> ReadUntilAsync<T>(
            Func<Task<T>> read,
            Func<T, bool> hasConverged,
            string operation,
            string expected)
            => ReminderTableRetryPolicy.ReadUntilAsync(
                read,
                hasConverged,
                _providerName,
                nameof(ReminderTableModelBasedTestRunner),
                operation,
                expected,
                value => value switch
                {
                    null => "null",
                    ReminderEntry entry => ReminderTableEntrySnapshot.Observe(entry, supportsSubSecondPrecision: true).ToString(),
                    ReminderTableData rows => $"{rows.Reminders.Count} rows: [{string.Join(", ", rows.Reminders.Select(entry => ReminderTableEntrySnapshot.Observe(entry, supportsSubSecondPrecision: true)))}]",
                    _ => value.ToString() ?? "<null>"
                },
                _cancellationToken);

        private bool Matches(ReminderTableEntrySnapshot expected, ReminderEntry actual)
            => ReminderTableEntrySnapshotComparer.CompareExact(
                [expected],
                [ReminderTableEntrySnapshot.Observe(actual, supportsSubSecondPrecision: true)]) is null;

        private bool Matches(IReadOnlyList<ReminderTableEntrySnapshot> expected, IEnumerable<ReminderEntry> actual)
            => ReminderTableEntrySnapshotComparer.CompareExact(
                expected,
                actual.Select(entry => ReminderTableEntrySnapshot.Observe(entry, supportsSubSecondPrecision: true)).ToList()) is null;

        private List<ReminderObservedEntry> ToObservedEntries(IEnumerable<ReminderEntry> rows)
        {
            var entries = new List<ReminderObservedEntry>();
            foreach (var reminder in rows)
            {
                string? logicalKey = null;
                foreach (var (key, identity) in _identities)
                {
                    if (identity.GrainId.Equals(reminder.GrainId) && string.Equals(identity.ReminderName, reminder.ReminderName, StringComparison.Ordinal))
                    {
                        logicalKey = key;
                        break;
                    }
                }

                entries.Add(new ReminderObservedEntry(
                    logicalKey,
                    ReminderTableEntrySnapshot.Observe(reminder, supportsSubSecondPrecision: true)));
            }

            return entries;
        }

        private static ReminderEntry CreateEntry(GrainId grainId, string reminderName, int schedule) => new()
        {
            GrainId = grainId,
            ReminderName = reminderName,
            StartAt = StartAtFor(schedule),
            Period = PeriodFor(schedule)
        };

        internal static DateTime StartAtFor(int schedule) => BaseTime.AddMinutes(schedule);

        internal static TimeSpan PeriodFor(int schedule) => TimeSpan.FromMinutes(schedule);
    }

    private sealed class ReminderRequest
    {
        public ReminderRequest(ReminderOperation operation, string key, int schedule = ScheduleId.One, ETagMode etagMode = ETagMode.None, RangeMode range = RangeMode.Full)
        {
            Operation = operation;
            Key = key;
            Schedule = schedule;
            ETagMode = etagMode;
            Range = range;
        }

        public string Key { get; }

        public int Schedule { get; }

        public ETagMode ETagMode { get; }

        public RangeMode Range { get; }

        public ReminderOperation Operation { get; }

        public override string ToString()
            => $"operation={Operation}, key={Key}, grain={ReminderKey.GrainOf(Key)}, schedule={Schedule.ToString(CultureInfo.InvariantCulture)}, etag={ETagMode}, range={Range}";
    }

    private sealed class ReminderOperationResult
    {
        private ReminderOperationResult(
            bool succeeded,
            string? exceptionType,
            string? etag,
            bool removed,
            IReadOnlyList<ReminderObservedEntry> entries)
        {
            Succeeded = succeeded;
            ExceptionType = exceptionType;
            ETag = etag;
            Removed = removed;
            Entries = entries;
        }

        public bool Succeeded { get; }

        public string? ExceptionType { get; }

        public string? ETag { get; }

        public bool Removed { get; }

        public IReadOnlyList<ReminderObservedEntry> Entries { get; }

        public static ReminderOperationResult Success(string? etag, bool removed, IReadOnlyList<ReminderObservedEntry> entries)
            => new(true, null, etag, removed, entries);

        public static ReminderOperationResult Upserted(ReminderRequest request, string etag)
            => new(
                true,
                null,
                etag,
                false,
                [
                    new ReminderObservedEntry(
                        request.Key,
                        new ReminderTableEntrySnapshot(
                            default,
                            ReminderKey.ReminderNameOf(request.Key),
                            ReminderExecutionContext.StartAtFor(request.Schedule).Ticks,
                            ReminderExecutionContext.PeriodFor(request.Schedule).Ticks,
                            etag))
                ]);

        public static ReminderOperationResult Failure(Exception exception)
        {
            var failure = exception is AggregateException { InnerExceptions.Count: 1 } aggregate
                ? aggregate.InnerExceptions[0]
                : exception;
            return new ReminderOperationResult(
                false,
                $"{failure.GetType().FullName}: {failure.Message}",
                null,
                false,
                []);
        }

        public override string ToString()
            => Succeeded
                ? $"success etag={ETag ?? "<null>"} removed={Removed.ToString(CultureInfo.InvariantCulture)} entries=[{string.Join(", ", Entries)}]"
                : $"failure exception={ExceptionType ?? "<null>"}";
    }

    private sealed class ReminderObservedEntry(string? logicalKey, ReminderTableEntrySnapshot snapshot)
    {
        public string? LogicalKey { get; } = logicalKey;

        public ReminderTableEntrySnapshot Snapshot { get; } = snapshot;

        public override string ToString()
            => $"{{ LogicalKey={LogicalKey ?? "<unknown>"}, Snapshot={Snapshot} }}";
    }

    private enum ReminderOperation
    {
        Upsert,
        ReadRow,
        ReadGrainRows,
        ReadRange,
        Remove,
        Clear
    }

    private enum ETagMode
    {
        None,
        Current,
        Stale,
        Missing
    }

    private enum RangeMode
    {
        Full,
        OtherGrainOnly,
        ExcludingOtherGrain
    }

    private static class ScheduleId
    {
        public const int One = 1;
        public const int Two = 2;
    }

    private static class ReminderKey
    {
        public const string First = "first";
        public const string SameGrain = "same-grain";
        public const string OtherGrain = "other-grain";

        public const string PrimaryGrainName = "grain-1";
        public const string OtherGrainName = "grain-2";

        public static string GrainOf(string key) => key == OtherGrain ? OtherGrainName : PrimaryGrainName;

        public static string ReminderNameOf(string key) => key == SameGrain ? "reminder-2" : "reminder-1";
    }
}

[State]
internal partial class ReminderTableModelState : State
{
    public Dictionary<string, ReminderModelRecord> Records { get; set; } = new(StringComparer.Ordinal);
}

[State]
internal partial class ReminderModelRecord : State
{
    public string LogicalKey { get; set; } = string.Empty;

    public string GrainIdentity { get; set; } = string.Empty;

    public string ReminderName { get; set; } = string.Empty;

    public long StartAtTicks { get; set; }

    public long PeriodTicks { get; set; }

    public string? ETag { get; set; }

    public string? PreviousETag { get; set; }

    public bool Exists { get; set; }

    public int Version { get; set; }
}
