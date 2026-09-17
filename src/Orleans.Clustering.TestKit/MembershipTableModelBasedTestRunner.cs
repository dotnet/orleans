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
    public async Task RunGeneratedConformanceTests(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var ct = timeout.Token;
        var covered = new HashSet<MembershipOperationKind>();
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var caseNumber = 0;
        await RunBatch(null);
        foreach (var prefix in RequiredPrefixes()) await RunBatch(prefix);
        var missing = Enum.GetValues<MembershipOperationKind>().Except(covered).ToArray();
        ClusteringTestKitDiagnostics.Require(missing.Length == 0,
            $"provider={_options.ProviderName}; seed={_options.Seed}; generated execution missed required operations: {string.Join(", ", missing)}");
        _output?.Invoke($"provider={_options.ProviderName}; seed={_options.Seed}; Accordant cases={caseNumber}; operations={string.Join(",", covered.Order())}");

        async Task RunBatch(MembershipRequest[]? prefix)
        {
            ct.ThrowIfCancellationRequested();
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
            var context = spec.CreateTestingContext();
            context.RequestPrinter = request => request?.ToString() ?? "<null>";
            context.ResponsePrinter = response => response?.ToString() ?? "<null>";
            MembershipTableTestFixture? current = null;
            Exception? cleanupFailure = null;
            Exception? primary = null;
            try
            {
                var results = await spec.RunTests(context, initial, cases, new TestExecutionOptions
                {
                    StopOnFirstFailure = true,
                    BeforeEach = info =>
                    {
                        ct.ThrowIfCancellationRequested();
                        current = _factory() ?? throw new InvalidOperationException("The model fixture factory returned null.");
                        ClusteringTestKitDiagnostics.Require(scopes.Add(current.ClusterId), "model factory reused a fixture/scope between cases");
                        // Accordant 0.1.6 hooks are synchronous. Bound the bridge; never use async-void callbacks.
                        var fixture = current;
                        Task.Run(() => fixture.InitializeAsync(ct).AsTask(), ct).WaitAsync(ct).GetAwaiter().GetResult();
                        info.Context.Register(new MembershipModelExecutionContext(current, _options.Seed, caseNumber++, ct, kind => covered.Add(kind)));
                    },
                    AfterEach = info =>
                    {
                        try
                        {
                            if (current is { } fixture)
                                Task.Run(() => fixture.DisposeAsync().AsTask()).GetAwaiter().GetResult();
                        }
                        catch (Exception exception) { cleanupFailure ??= exception; }
                        current = null;
                        if (!info.Success) _output?.Invoke(info.FailureMessage);
                    }
                }).WaitAsync(ct);
                var failure = results.FirstOrDefault(result => !result.Success);
                if (failure is not null)
                    primary = new ClusteringConformanceException($"provider={_options.ProviderName}; seed={_options.Seed}; executed cases={caseNumber}; {failure.LastFailureMessage}");
                ClusteringTestKitDiagnostics.Require(results.Count > 0, "Accordant generated/executed no cases");
            }
            catch (Exception exception) { primary = exception; }
            finally
            {
                if (current is not null)
                {
                    try { await current.DisposeAsync(); }
                    catch (Exception exception) { cleanupFailure ??= exception; }
                }
            }

            if (cleanupFailure is not null)
            {
                if (primary is null) primary = cleanupFailure;
                else ClusteringTestKitDiagnostics.AttachCleanupFailure(primary, cleanupFailure);
            }
            if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }

    internal static IEnumerable<MembershipRequest[]> RequiredPrefixes()
    {
        yield return [new(MembershipOperationKind.InsertNew, 1), new(MembershipOperationKind.InsertNew, 2), new(MembershipOperationKind.UpdateStaleTable, 1)];
        yield return [new(MembershipOperationKind.InsertNew), new(MembershipOperationKind.UpdateForward), new(MembershipOperationKind.UpdateStaleRow)];
        yield return [new(MembershipOperationKind.InsertNew), new(MembershipOperationKind.HeartbeatNewer), new(MembershipOperationKind.HeartbeatOlder)];
        yield return [new(MembershipOperationKind.InsertNew), new(MembershipOperationKind.HeartbeatNewer), new(MembershipOperationKind.UpdateWithOldHeartbeat)];
        yield return [new(MembershipOperationKind.InsertNew), new(MembershipOperationKind.DeleteCluster),
            new(MembershipOperationKind.Initialize), new(MembershipOperationKind.InsertNew), new(MembershipOperationKind.ReadPresentRow)];
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
