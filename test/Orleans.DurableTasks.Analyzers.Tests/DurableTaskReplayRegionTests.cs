using Xunit;

namespace Orleans.DurableTasks.Analyzers.Tests;

/// <summary>Tests the replay region rules: state, non-determinism, external awaits, and state changes in the tail.</summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class DurableTaskReplayRegionTests
{
    private const string Members = """
            private static readonly DurableSignal<string> Shipped = new("shipped");
            private readonly Orleans.Runtime.IPersistentState<int> _status = null!;
            private readonly Orleans.Runtime.IPersistentState<Dictionary<int, bool>> _stock = null!;

            private static DurableTask<int> Step(int value) => DurableTask.FromResult(value);

            private static Task<int> FetchAsync() => Task.FromResult(1);

            private int SetStatus(int status)
            {
                _status.State = status;
                return status;
            }
        """;

    [Fact]
    public Task CanonicalWorkflowReportsNothing() => AnalyzerVerifier.VerifyAsync($$"""
        public class OrderGrain : Orleans.Grain
        {
        {{Members}}
            private readonly ILogger _log;

            public OrderGrain(ILogger logger) => _log = logger.ReplaySafe();

            public async DurableTask<int> PlaceOrderAsync(string buyerId, int[] lines)
            {
                var summary = DurableTask.RunInline("submit", () => SetStatus(1));
                Record(buyerId, summary);
                _log.LogInformation("Submitted at {Time}", DateTime.UtcNow);
                bool[] confirmed;
                try
                {
                    await DurableTask.Delay(TimeSpan.FromSeconds(10)).WithId("grace-period");
                    summary = DurableTask.RunInline("validate", () => SetStatus(2));
                    confirmed = await DurableTask.WhenAll(lines.Select(line => Reserve(line))).WithId("reserve");
                }
                catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
                {
                    return await CancelOrderAsync(buyerId);
                }

                if (Array.IndexOf(confirmed, false) >= 0)
                {
                    return await CancelOrderAsync(buyerId);
                }

                await Shipped.WaitAsync();
                summary = DurableTask.RunInline("ship", () => SetStatus(3));
                Record(buyerId, summary);

                // The tail runs once, with the run which completes the body.
                _status.State = summary;
                return summary;
            }

            public async Task<bool> ShipAsync()
            {
                if (_status.State != 3)
                {
                    return false;
                }

                await FetchAsync();
                return true;
            }

            private async Task<int> CancelOrderAsync(string buyerId)
            {
                var summary = DurableTask.RunInline("cancel", () => SetStatus(4));
                await Step(summary).WithId("release").Shielded();
                Record(buyerId, summary);
                return summary;
            }

            private static void Record(string buyerId, int summary) =>
                Step(summary).Detached($"buyer-{buyerId}-v{summary}").Post();

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
                var status = {|ORLEANS0200:_status|}.State;
                await Step(status).WithId("first");
                if ({|ORLEANS0200:_stock|}.State.ContainsKey(1))
                {
                    await Step(2).WithId("second");
                }

                {|ORLEANS0200:_status|}.State = 2;
                var recorded = DurableTask.RunInline("read", () => _status.State);
                await Step(recorded).WithId("third");
                return _status.State;
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
                var status = {|ORLEANS0200:_status|}.State;
                var id = DurableTask.RunInline(Guid.NewGuid);
                var read = DurableTask.{|ORLEANS0220:RunInline|}(() => _status.State);
                var now = DurableTask.{|ORLEANS0220:RunInline|}(() => DateTime.UtcNow);
                DurableTask.{|ORLEANS0220:RunInline|}(() => SetStatus(status + read));

                // The tail follows the last recorded effect.
                return $"{id} {now} {_status.State}";
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
                    {|ORLEANS0200:_stock|}.State = new() { [i] = true };
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
                var id = {|ORLEANS0201:Guid.NewGuid()|};
                var now = {|ORLEANS0201:DateTime.UtcNow|};
                var random = {|ORLEANS0201:new Random()|};
                var recorded = DurableTask.RunInline("id", Guid.NewGuid);
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
                var fetched = {|ORLEANS0202:await FetchAsync()|};
                {|ORLEANS0211:await Task.Delay(1000)|};
                await Task.Yield();
                await ScheduleHelperAsync();
                await Step(fetched).WithId("first");
                return await FetchAsync();
            }

            private Task<ScheduledTask<int>> ScheduleHelperAsync() => Step(1).Detached().ScheduleAsync();
        }
        """);

    [Fact]
    public Task JournaledStateIsRecordedAndPersistentStateIsGrainState() => AnalyzerVerifier.VerifyAsync($$"""
        using Orleans.Runtime;

        public class C
        {
        {{Members}}
            private readonly IDurableValue<int> _value = null!;
            private readonly IDurableDictionary<int, bool> _dictionary = null!;
            private readonly IDurableList<int> _list = null!;
            private readonly IDurableQueue<int> _queue = null!;
            private readonly IDurableSet<int> _set = null!;
            private readonly IDurableTaskCompletionSource<int> _completion = null!;
            private readonly IPersistentState<int> _state = null!;

            public async DurableTask<int> RunAsync()
            {
                // The runtime records the accesses of journaled state, so a replay returns their recorded outcomes.
                _value.Value = _value.Value + 1;
                _dictionary[1] = !_dictionary.ContainsKey(1);
                _list.Add(1);
                _queue.Enqueue(1);
                _set.Add(1);
                _completion.TrySetResult(1);
                {|ORLEANS0200:_state|}.State = 1;

                var count = DurableTask.RunInline("progress", () => { _queue.Enqueue(2); return _queue.Count; });
                await Step(1).WithId("step");
                _queue.Enqueue(3);
                return count;
            }
        }
        """);

    [Fact]
    public Task JournaledStateChangesBeforeAnExternalAwaitAreRecorded() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            private readonly IDurableList<int> _list = null!;
            private readonly IDurableSet<int> _set = null!;
            private readonly IDurableQueue<int> _queue = null!;

            public async DurableTask<int> StepAsync()
            {
                _list.AddRange(new[] { 1, 2 });
                _set.UnionWith(new[] { 1, 2 });
                _set.ExceptWith(new[] { 3 });
                _queue.Enqueue(1);
                _queue.Dequeue();
                await FetchAsync();
                _queue.Enqueue(2);
                return _queue.Count;
            }
        }
        """);

    [Fact]
    public Task StateChangesBeforeAnExternalAwaitInTheTailAreReported() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> StepAsync()
            {
                {|ORLEANS0203:_status.State = 1|};
                {|ORLEANS0203:_status.State++|};
                await FetchAsync();
                _status.State = 2;
                _status.State--;
                return 2;
            }

            public async DurableTask<int> TailAsync()
            {
                await Step(1).WithId("first");
                var fetched = await FetchAsync();
                {|ORLEANS0203:_status.State = fetched|};
                await FetchAsync();
                return fetched;
            }
        }
        """);

    [Fact]
    public Task PersistentStateWritesBeforeAnExternalAwaitAreReported() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            private readonly IOrderState<int> _order = null!;

            public async DurableTask<int> StepAsync()
            {
                await {|ORLEANS0203:_status.WriteStateAsync()|};
                await {|ORLEANS0203:_order.WriteStateAsync()|};
                await FetchAsync();
                await _status.WriteStateAsync();
                return 1;
            }
        }

        public interface IOrderState<T> : Orleans.Runtime.IPersistentState<T>
        {
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
                    var value = {|ORLEANS0202:await FetchAsync()|};
                    await Step(value).WithId("inner");
                    return value;
                }).WithId("fetch");

                var tokenless = await DurableTask.Run(async () =>
                {
                    var value = {|ORLEANS0202:await FetchAsync()|};
                    await Step(value).WithId("tokenless-inner");
                    return value + DateTime.UtcNow.Second;
                }).WithId("tokenless");

                return fetched + tokenless + await Local();

                async DurableTask<int> Local()
                {
                    var status = {|ORLEANS0200:_status|}.State;
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
                var status = {|ORLEANS0200:_status|}.State;
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
                var status = {|ORLEANS0200:_status|}.State;
                var (index, value) = await DurableTask.Race(new[] { Step(status), Step(2) }).WithId("race");
                return index + value;
            }

            public async DurableTask<int> TimeoutAsync()
            {
                var status = {|ORLEANS0200:_status|}.State;
                return await Step(status).WaitAsync(TimeSpan.FromMinutes(1)).WithId("pay");
            }

            public async DurableTask<int> HandleTimeoutAsync(ScheduledTask<int> payment)
            {
                var status = {|ORLEANS0200:_status|}.State;
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
                var status = _status.State;
                var id = Guid.NewGuid();
                await FetchAsync();
                await Step(status).WithId(id.ToString()).ScheduleAsync();
                return status;
            }
        }
        """);
}
