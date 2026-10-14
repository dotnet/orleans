using Xunit;

namespace Orleans.DurableTasks.Analyzers.Tests;

/// <summary>Tests the replay region rules: state, non-determinism, external awaits, and state changes in the tail.</summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class DurableTaskReplayRegionTests
{
    private const string Members = """
            private static readonly DurableSignal<string> Shipped = new("shipped");
            private readonly IDurableValue<int> _status = null!;
            private readonly IDurableDictionary<int, bool> _stock = null!;

            private static DurableTask<int> Step(int value) => DurableTask.FromResult(value);

            private static Task<int> FetchAsync() => Task.FromResult(1);

            private int SetStatus(int status)
            {
                _status.Value = status;
                return status;
            }
        """;

    [Fact]
    public Task CanonicalWorkflowReportsNothing() => AnalyzerVerifier.VerifyAsync($$"""
        public class OrderGrain
        {
        {{Members}}
            private readonly ILogger _log;

            public OrderGrain(ILogger logger) => _log = logger.ReplaySafe();

            public async DurableTask<int> PlaceOrderAsync(string buyerId, int[] lines)
            {
                var summary = DurableTask.Once("submit", () => SetStatus(1));
                await RecordAsync(buyerId, summary);
                _log.LogInformation("Submitted at {Time}", DateTime.UtcNow);
                bool[] confirmed;
                try
                {
                    await DurableTask.Delay(TimeSpan.FromSeconds(10)).WithId("grace-period");
                    summary = DurableTask.Once("validate", () => SetStatus(2));
                    confirmed = await DurableTask.WhenAll(lines.Select(line => Reserve(line))).WithId("reserve");
                }
                catch (OperationCanceledException) when (DurableTask.Current.IsCancellationRequested)
                {
                    return await CancelOrderAsync(buyerId);
                }

                if (Array.IndexOf(confirmed, false) >= 0)
                {
                    return await CancelOrderAsync(buyerId);
                }

                await Shipped.WaitAsync();
                summary = DurableTask.Once("ship", () => SetStatus(3));
                await RecordAsync(buyerId, summary);

                // The tail runs once, with the run which completes the body.
                _status.Value = summary;
                return summary;
            }

            public async Task<bool> ShipAsync()
            {
                if (_status.Value != 3)
                {
                    return false;
                }

                await FetchAsync();
                return true;
            }

            private async Task<int> CancelOrderAsync(string buyerId)
            {
                using (DurableTask.Current.SuppressCancellation())
                {
                    var summary = DurableTask.Once("cancel", () => SetStatus(4));
                    await RecordAsync(buyerId, summary);
                    return summary;
                }
            }

            private ValueTask RecordAsync(string buyerId, int summary) =>
                DurableTask.FromResult(summary).DetachAsync($"buyer-{buyerId}-v{summary}");

            private static DurableTask<bool> Reserve(int line) => DurableTask.FromResult(line > 0);
        }
        """);

    [Fact]
    public Task GrainStateBeforeADurableOperationIsReported() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync()
            {
                var status = {|ORLEANS0100:_status|}.Value;
                await Step(status).WithId("first");
                if ({|ORLEANS0100:_stock|}.ContainsKey(1))
                {
                    await Step(2).WithId("second");
                }

                {|ORLEANS0100:_status|}.Value = 2;
                var recorded = DurableTask.Once("read", () => _status.Value);
                await Step(recorded).WithId("third");
                return _status.Value;
            }
        }
        """);

    [Fact]
    public Task UnnamedEffectsAreRecordedOperations() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<string> RunAsync()
            {
                var status = {|ORLEANS0100:_status|}.Value;
                var id = DurableTask.Once(Guid.NewGuid);
                var read = DurableTask.Once(() => _status.Value);
                var now = DurableTask.Once(() => DateTime.UtcNow);
                DurableTask.Once(() => SetStatus(status + read));

                // The tail follows the last recorded effect.
                return $"{id} {now} {_status.Value}";
            }
        }
        """);

    [Fact]
    public Task GrainStateInALoopWhichSchedulesDurableWorkIsReported() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask RunAsync()
            {
                for (var i = 0; i < 3; i++)
                {
                    await Step(i).WithId($"step-{i}");
                    {|ORLEANS0100:_stock|}[i] = true;
                }
            }
        }
        """);

    [Fact]
    public Task NonDeterministicValuesBeforeADurableOperationAreReported() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<string> RunAsync(ILogger logger)
            {
                var id = {|ORLEANS0101:Guid.NewGuid()|};
                var now = {|ORLEANS0101:DateTime.UtcNow|};
                var random = {|ORLEANS0101:new Random()|};
                var recorded = DurableTask.Once("id", Guid.NewGuid);
                var logical = DurableTask.Current.UtcNow;
                await Step(random.Next()).WithId("first");
                return $"{id} {now} {recorded} {logical} {Guid.NewGuid()}";
            }
        }
        """);

    [Fact]
    public Task ExternalAwaitsBeforeADurableOperationAreReported() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync()
            {
                var fetched = {|ORLEANS0102:await FetchAsync()|};
                {|ORLEANS0111:await Task.Delay(1000)|};
                await Task.Yield();
                await DetachHelperAsync();
                await Step(fetched).WithId("first");
                return await FetchAsync();
            }

            private ValueTask DetachHelperAsync() => Step(1).DetachAsync("detached");
        }
        """);

    [Fact]
    public Task StateChangesBeforeAnExternalAwaitInTheTailAreReported() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> StepAsync()
            {
                {|ORLEANS0103:_status.Value = 1|};
                await FetchAsync();
                _status.Value = 2;
                return 2;
            }

            public async DurableTask<int> TailAsync()
            {
                await Step(1).WithId("first");
                var fetched = await FetchAsync();
                {|ORLEANS0103:_status.Value = fetched|};
                {|ORLEANS0103:_stock.Add(1, true)|};
                await FetchAsync();
                return fetched;
            }
        }
        """);

    [Fact]
    public Task RunDelegatesAndLocalFunctionsAreBodies() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync()
            {
                var fetched = await DurableTask.Run(async _ =>
                {
                    var value = {|ORLEANS0102:await FetchAsync()|};
                    await Step(value).WithId("inner");
                    return value;
                }).WithId("fetch");

                return await Local();

                async DurableTask<int> Local()
                {
                    var status = {|ORLEANS0100:_status|}.Value;
                    await Step(status).WithId("local");
                    return status;
                }
            }
        }
        """);

    [Fact]
    public Task AsyncEnumerationOfDurableTasksIsADurableOperation() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync(int[] items)
            {
                var status = {|ORLEANS0100:_status|}.Value;
                await foreach (var completed in DurableTask.WhenEach(items.Select(item => (DurableTask)Step(item))))
                {
                }

                return status;
            }
        }
        """);

    [Fact]
    public Task RacesAndTimeoutsAreDurableOperations() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RaceAsync()
            {
                var status = {|ORLEANS0100:_status|}.Value;
                var (index, value) = await DurableTask.Race(new[] { Step(status), Step(2) }).WithId("race");
                return index + value;
            }

            public async DurableTask<int> TimeoutAsync()
            {
                var status = {|ORLEANS0100:_status|}.Value;
                return await Step(status).WaitAsync(TimeSpan.FromMinutes(1)).WithId("pay");
            }

            public async DurableTask<int> HandleTimeoutAsync(ScheduledTask<int> payment)
            {
                var status = {|ORLEANS0100:_status|}.Value;
                return status + await payment.WaitUntilAsync(DurableTask.Current.UtcNow.AddMinutes(1));
            }
        }
        """);

    [Fact]
    public Task OrdinaryMethodsAreNotAnalyzed() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async Task<int> RunAsync()
            {
                var status = _status.Value;
                var id = Guid.NewGuid();
                await FetchAsync();
                await Step(status).ScheduleAsync(id.ToString());
                return status;
            }
        }
        """);
}
