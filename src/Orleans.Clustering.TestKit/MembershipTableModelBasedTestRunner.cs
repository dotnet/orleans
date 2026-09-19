using Microsoft.Accordant;

namespace Orleans.Clustering.TestKit;

/// <summary>Bounds generated membership operation-sequence exploration.</summary>
public sealed class MembershipTableModelBasedConformanceOptions
{
    /// <summary>Gets or sets the non-secret provider label.</summary>
    public string ProviderName { get; set; } = "MembershipTable";
    /// <summary>Gets or sets the reproducible identity seed.</summary>
    public int Seed { get; set; }
    /// <summary>Gets or sets general transition exploration depth (at least three).</summary>
    public int MaxDepth { get; set; } = 3;
    /// <summary>Gets or sets general generated sequence length (at least three).</summary>
    public int MaxSequenceLength { get; set; } = 3;
}

/// <summary>Runs actual Accordant transition generation/execution in fresh factory-owned scopes per case.</summary>
public sealed class MembershipTableModelBasedTestRunner
{
    private readonly Func<MembershipTableTestFixture> _factory;
    private readonly MembershipTableModelBasedConformanceOptions _options;
    private readonly Action<string>? _output;

    /// <summary>Creates a generated runner using default bounded exploration.</summary>
    public MembershipTableModelBasedTestRunner(Func<MembershipTableTestFixture> fixtureFactory, string providerName, Action<string>? output = null)
        : this(fixtureFactory, new MembershipTableModelBasedConformanceOptions { ProviderName = providerName }, output) { }

    /// <summary>Creates a generated runner. The factory must return a new fixture on every call.</summary>
    public MembershipTableModelBasedTestRunner(Func<MembershipTableTestFixture> fixtureFactory, MembershipTableModelBasedConformanceOptions options, Action<string>? output = null)
    {
        _factory = fixtureFactory ?? throw new ArgumentNullException(nameof(fixtureFactory));
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderName);
        if (options.MaxDepth is < 3 or > 8) throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must be in [3,8].");
        if (options.MaxSequenceLength is < 3 or > 32) throw new ArgumentOutOfRangeException(nameof(options), "MaxSequenceLength must be in [3,32].");
        _options = new() { ProviderName = options.ProviderName, Seed = options.Seed, MaxDepth = options.MaxDepth, MaxSequenceLength = options.MaxSequenceLength };
        _output = output;
    }

    /// <summary>Generates legal transitions, required reachable prefixes, and verifies every operation's expected state.</summary>
    public Task RunGeneratedConformanceTests(CancellationToken cancellationToken = default)
        => RunGeneratedConformanceTests(0, 1, cancellationToken);

    internal async Task RunGeneratedConformanceTests(int partitionIndex, int partitionCount, CancellationToken cancellationToken)
    {
        ValidatePartition(partitionIndex, partitionCount);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        var batches = SelectPartition(GenerateBatches(ct), partitionIndex, partitionCount);
        var expectedCases = batches.SelectMany(batch => batch.Cases).ToArray();
        var expectedOperations = Enum.GetValues<MembershipOperationKind>().ToDictionary(kind => kind, _ => 0);
        foreach (var testCase in expectedCases)
        {
            foreach (var call in testCase.TestCase.OperationCalls)
                expectedOperations[((MembershipRequest)call.OperationInput.Request).Kind]++;
        }
        var executedCases = new Dictionary<int, int>();
        var executedOperations = Enum.GetValues<MembershipOperationKind>().ToDictionary(kind => kind, _ => 0);
        var covered = new HashSet<MembershipOperationKind>();
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var caseNumber = 0;
        foreach (var batch in batches) await RunBatch(batch);
        ClusteringTestKitDiagnostics.Require(caseNumber == expectedCases.Length
            && executedCases.Count == expectedCases.Length
            && expectedCases.All(testCase => executedCases.GetValueOrDefault(testCase.Index) == 1),
            $"provider={_options.ProviderName}; seed={_options.Seed}; partition={partitionIndex}/{partitionCount}; expected cases={expectedCases.Length}, executed cases={caseNumber}");
        foreach (var (kind, expected) in expectedOperations)
        {
            ClusteringTestKitDiagnostics.Require(executedOperations[kind] == expected,
                $"provider={_options.ProviderName}; seed={_options.Seed}; partition={partitionIndex}/{partitionCount}; operation={kind}; expected count={expected}, executed count={executedOperations[kind]}");
        }
        if (partitionCount == 1)
        {
            var missing = Enum.GetValues<MembershipOperationKind>().Except(covered).ToArray();
            ClusteringTestKitDiagnostics.Require(missing.Length == 0,
                $"provider={_options.ProviderName}; seed={_options.Seed}; generated execution missed required operations: {string.Join(", ", missing)}");
        }
        _output?.Invoke($"provider={_options.ProviderName}; seed={_options.Seed}; Accordant cases={caseNumber}; operations={string.Join(",", covered.Order())}; partition={partitionIndex}/{partitionCount}; operation-counts={string.Join(",", executedOperations.Select(pair => $"{pair.Key}:{pair.Value}"))}");

        async Task RunBatch(GeneratedBatch batch)
        {
            ct.ThrowIfCancellationRequested();
            var spec = batch.Spec;
            var initial = batch.InitialState;
            var cases = batch.TestCases;
            var context = spec.CreateTestingContext();
            context.RequestPrinter = request => request?.ToString() ?? "<null>";
            context.ResponsePrinter = response => response?.ToString() ?? "<null>";
            MembershipTableTestFixture? current = null;
            var currentCase = 0;
            Exception? cleanupFailure = null;
            Exception? primary = null;
            try
            {
                var results = await spec.RunTests(context, initial, cases, new TestExecutionOptions
                {
                    StopOnFirstFailure = true,
                    BeforeEachAsync = async info =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (cleanupFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                        var generated = batch.Cases[info.TestIndex];
                        currentCase = generated.Index;
                        caseNumber++;
                        executedCases[generated.Index] = executedCases.GetValueOrDefault(generated.Index) + 1;
                        current = _factory() ?? throw new InvalidOperationException("The model fixture factory returned null.");
                        ClusteringTestKitDiagnostics.Require(scopes.Add(current.ClusterId), "model factory reused a fixture/scope between cases");
                        ReportProgress("initialize", current);
                        // Preserve the caller's bound when a provider callback blocks synchronously.
                        var fixture = current;
                        await Task.Run(() => fixture.InitializeAsync(ct).AsTask(), ct).WaitAsync(ct);
                        ReportProgress("initialized", current);
                        info.Context.Register(new MembershipModelExecutionContext(current, _options.Seed, currentCase, ct, kind =>
                        {
                            covered.Add(kind);
                            executedOperations[kind]++;
                        }, _output));
                    },
                    AfterEachAsync = async info =>
                    {
                        if (!info.Success)
                            primary ??= new ClusteringConformanceException($"provider={_options.ProviderName}; seed={_options.Seed}; executed cases={caseNumber}; {info.FailureMessage}");
                        try
                        {
                            if (current is { } fixture)
                            {
                                try { ReportProgress("dispose", fixture); }
                                finally { await fixture.DisposeAsync(); }
                                ReportProgress("disposed", fixture);
                            }
                        }
                        catch (Exception exception)
                        {
                            cleanupFailure ??= exception;
                            throw;
                        }
                        finally
                        {
                            current = null;
                            if (!info.Success) _output?.Invoke(info.FailureMessage);
                        }
                    }
                }).WaitAsync(ct);
                var failure = results.FirstOrDefault(result => !result.Success);
                if (failure is not null)
                    primary = new ClusteringConformanceException($"provider={_options.ProviderName}; seed={_options.Seed}; executed cases={caseNumber}; {failure.LastFailureMessage}");
                ClusteringTestKitDiagnostics.Require(results.Count > 0, "Accordant generated/executed no cases");
            }
            catch (Exception exception) { primary ??= exception; }
            finally
            {
                if (current is { } fixture)
                {
                    try
                    {
                        try { ReportProgress("dispose", fixture); }
                        finally { await fixture.DisposeAsync(); }
                        ReportProgress("disposed", fixture);
                    }
                    catch (Exception exception) { cleanupFailure ??= exception; }
                }
            }

            if (cleanupFailure is not null)
            {
                if (primary is null) primary = cleanupFailure;
                else if (!ReferenceEquals(primary, cleanupFailure)) ClusteringTestKitDiagnostics.AttachCleanupFailure(primary, cleanupFailure);
            }
            if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();

            void ReportProgress(string phase, MembershipTableTestFixture fixture)
                => _output?.Invoke($"seed={_options.Seed}; case={currentCase}; phase={phase}; cluster={fixture.ClusterId}");
        }
    }

    internal sealed record GeneratedCase(int Index, string Identity, SequentialTestCase TestCase);

    internal sealed record GeneratedBatch(
        MembershipBehavioralSpec Spec,
        MembershipModelState InitialState,
        IList<SequentialTestCase> TestCases,
        IReadOnlyList<GeneratedCase> Cases);

    internal IReadOnlyList<GeneratedBatch> GenerateBatches(CancellationToken cancellationToken)
    {
        var result = new List<GeneratedBatch>();
        var caseIndex = 0;
        GenerateBatch(null);
        foreach (var prefix in RequiredPrefixes()) GenerateBatch(prefix);
        return result;

        void GenerateBatch(MembershipRequest[]? prefix)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var spec = new MembershipBehavioralSpec();
            var initial = new MembershipModelState();
            var cases = spec.GenerateTests(initial, spec.CreateInputSet(prefix?.Distinct()), new TestGenerationOptions
            {
                // Accordant counts the initial state toward exploration depth.
                MaxDepth = prefix is null ? _options.MaxDepth : prefix.Length + 1,
                SequentialTestCaseAlgorithm = SequentialTestCaseAlgorithms.CreateTransitionCoverage(prefix?.Length ?? _options.MaxSequenceLength),
                ShouldApply = (input, state) =>
                {
                    var request = (MembershipRequest)input.Request;
                    var model = (MembershipModelState)state;
                    return MembershipModel.CanApply(request, model)
                        && (prefix is null || (model.Steps < prefix.Length && prefix[model.Steps] == request));
                }
            });
            var manifest = CreateCaseManifest(result.Count, caseIndex, cases);
            caseIndex += cases.Count;
            result.Add(new(spec, initial, cases, manifest));
        }
    }

    internal static IReadOnlyList<GeneratedCase> CreateCaseManifest(
        int batchIndex, int firstCaseIndex, IList<SequentialTestCase> cases)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var manifest = new List<GeneratedCase>(cases.Count);
        foreach (var testCase in cases)
        {
            var operations = string.Join(";", testCase.OperationCalls.Select(call =>
            {
                var request = (MembershipRequest)call.OperationInput.Request;
                return FormattableString.Invariant($"{call.OperationInput.Operation.Name}:{(int)request.Kind}:{request.Key}");
            }));
            var occurrence = occurrences.GetValueOrDefault(operations);
            occurrences[operations] = occurrence + 1;
            var identity = FormattableString.Invariant($"{batchIndex:D2}|{operations}|{occurrence:D4}");
            manifest.Add(new(firstCaseIndex++, identity, testCase));
        }
        return manifest;
    }

    internal static IReadOnlyList<GeneratedBatch> SelectPartition(
        IReadOnlyList<GeneratedBatch> batches, int partitionIndex, int partitionCount)
    {
        ValidatePartition(partitionIndex, partitionCount);
        var cases = batches.SelectMany(batch => batch.Cases).OrderBy(testCase => testCase.Identity, StringComparer.Ordinal).ToArray();
        if (partitionCount > cases.Length)
            throw new ArgumentOutOfRangeException(nameof(partitionCount), "Partition count cannot exceed the generated case count.");
        if (partitionCount == 1) return batches;
        // Ranking canonical identities balances whole cases without relying on process-randomized hash codes.
        var selected = cases.Where((_, index) => index % partitionCount == partitionIndex)
            .Select(testCase => testCase.Identity).ToHashSet(StringComparer.Ordinal);
        return batches.Select(batch =>
        {
            var partition = batch.Cases.Where(testCase => selected.Contains(testCase.Identity)).ToArray();
            return batch with { Cases = partition, TestCases = partition.Select(testCase => testCase.TestCase).ToList() };
        }).Where(batch => batch.Cases.Count > 0).ToArray();
    }

    private static void ValidatePartition(int partitionIndex, int partitionCount)
    {
        if (partitionCount < 1) throw new ArgumentOutOfRangeException(nameof(partitionCount));
        if (partitionIndex < 0 || partitionIndex >= partitionCount) throw new ArgumentOutOfRangeException(nameof(partitionIndex));
    }

    internal static IEnumerable<MembershipRequest[]> RequiredPrefixes()
    {
        yield return [new(MembershipOperationKind.InsertNew, 1), new(MembershipOperationKind.InsertNew, 2), new(MembershipOperationKind.UpdateStaleTable, 1)];
        yield return [new(MembershipOperationKind.InsertNew), new(MembershipOperationKind.UpdateForward), new(MembershipOperationKind.UpdateStaleSnapshot)];
        yield return [new(MembershipOperationKind.InsertNew), new(MembershipOperationKind.HeartbeatAdvance),
            new(MembershipOperationKind.HeartbeatAdvance), new(MembershipOperationKind.HeartbeatRepeat)];
        yield return [new(MembershipOperationKind.InsertNew), new(MembershipOperationKind.HeartbeatAdvance),
            new(MembershipOperationKind.HeartbeatAdvance), new(MembershipOperationKind.UpdateAfterHeartbeat)];
        yield return [new(MembershipOperationKind.InsertNew), new(MembershipOperationKind.UpdateForward), new(MembershipOperationKind.DeleteCluster)];
        yield return [new(MembershipOperationKind.InsertNew), new(MembershipOperationKind.UpdateForward),
            new(MembershipOperationKind.UpdateForward), new(MembershipOperationKind.UpdateForward),
            new(MembershipOperationKind.UpdateForward), new(MembershipOperationKind.UpdateForward),
            new(MembershipOperationKind.CleanupDead), new(MembershipOperationKind.StartSuccessor)];
        yield return new[] { new MembershipRequest(MembershipOperationKind.InsertNew, 1), new MembershipRequest(MembershipOperationKind.InsertNew, 2) }
            .Concat(Enumerable.Repeat(new MembershipRequest(MembershipOperationKind.UpdateForward, 1), 5))
            .Concat(Enumerable.Repeat(new MembershipRequest(MembershipOperationKind.UpdateForward, 2), 5))
            .Concat([new MembershipRequest(MembershipOperationKind.CleanupDead), new MembershipRequest(MembershipOperationKind.StartSuccessor, 1)])
            .ToArray();
    }
}
