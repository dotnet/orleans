using Xunit;

namespace Orleans.DurableTasks.Analyzers.Tests;

/// <summary>Tests the rules for effects, definitions, names, scheduling, cancellation, signals, and logging.</summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class DurableTaskUsageRuleTests
{
    private const string Members = """
            private static readonly DurableSignal<string> Approved = new("approved");
            private readonly IDurableValue<int> _status = null!;

            private static DurableTask<int> Step(int value) => DurableTask.FromResult(value);

            private static async DurableTask<int> LocalStep(int value) => value;

            private static Task<int> FetchAsync() => Task.FromResult(1);
        """;

    [Fact]
    public Task RunInlineEffectsReturnTheirOutputs() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync()
            {
                var captured = 0;
                DurableTask.RunInline("assign", () => { {|ORLEANS0104:captured = 1|}; });
                DurableTask.RunInline("increment", () => { {|ORLEANS0104:captured++|}; });
                var returned = DurableTask.RunInline("return", () =>
                {
                    var local = 2;
                    local++;
                    _status.Value = local;
                    return local;
                });
                await Step(captured + returned).WithId("step");
                return returned;
            }
        }
        """);

    [Fact]
    public Task UnnamedRunInlineEffectsReturnTheirOutputsAndHaveNoNames() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync()
            {
                var captured = 0;
                DurableTask.RunInline(() => { {|ORLEANS0104:captured = 1|}; });
                DurableTask.RunInline(() => { {|ORLEANS0104:captured++|}; });
                var returned = DurableTask.RunInline(() =>
                {
                    var local = 2;
                    local++;
                    _status.Value = local;
                    return local;
                });
                DurableTask.RunInline(() => {|ORLEANS0109:Task.Run(() => 1)|}.Result);

                // Unnamed effects take generated identifiers, so neither a result which contains '$' nor a repeated call
                // site is a name.
                DurableTask.RunInline(() => "$value");
                DurableTask.RunInline(() => 3);
                DurableTask.RunInline(() => 3);
                await Step(captured + returned).WithId("step");
                return returned;
            }
        }
        """);

    [Fact]
    public Task DefinitionsAreAwaitedScheduledOrPosted() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask RunAsync()
            {
                {|ORLEANS0105:Step(1);|}
                {|ORLEANS0105:_ = LocalStep(2);|}
                {|ORLEANS0105:Step(3).WithId("unobserved");|}
                {|ORLEANS0105:Step(3).Detached().Shielded();|}
                await Step(4).WithId("awaited");
                Step(5).Detached().Post();
                _ = await Step(5).ScheduleAsync();
                var definition = Step(6);
                await definition.WithId("later");
            }
        }
        """);

    [Fact]
    public Task SingleUseDefinitionsRunOnce() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask RunAsync(int[] items)
            {
                var once = LocalStep(1);
                await once.WithId("first");
                await {|ORLEANS0106:once|}.WithId("second");

                var outside = LocalStep(2);
                foreach (var item in items)
                {
                    await {|ORLEANS0106:outside|}.WithId($"loop-{item}");
                }

                foreach (var item in items)
                {
                    var inside = LocalStep(item);
                    await inside.WithId($"inside-{item}");
                }

                var reusable = DurableTask.Run(_ => 3);
                await reusable.WithId("run-1");
                await reusable.WithId("run-2");

                var configured = LocalStep(3).Detached();
                configured.Post();
                {|ORLEANS0106:configured|}.Post();
            }
        }
        """);

    [Fact]
    public Task NamesExcludeTheGeneratedIdentifierMarker() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask RunAsync()
            {
                await Step(1).WithId({|ORLEANS0107:"line$2"|});
                DurableTask.RunInline({|ORLEANS0107:"$effect"|}, () => 1);
                await Step(2).WithId("valid");
            }
        }
        """);

    [Fact]
    public Task NamesOutsideDurableTaskBodiesNameRootTasks() => AnalyzerVerifier.VerifyAsync($$"""
        public class Client
        {
        {{Members}}
            public async Task StartAsync()
            {
                await Step(1).WithId("order$1");
                await Step(2).WithId("order");
                await Step(3).WithId("order");
            }

            // A DurableTask.RunInline name identifies an effect only in a durable task, which runs this helper.
            private static int Record() => DurableTask.RunInline({|ORLEANS0107:"record$1"|}, () => 1);
        }
        """);

    [Fact]
    public Task EachCallSiteWhichOneRunReachesHasItsOwnName() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask RunAsync(bool condition, int[] items)
            {
                await Step(1).WithId("pay");
                await Step(2).WithId({|ORLEANS0108:"pay"|});
                DurableTask.RunInline({|ORLEANS0108:"pay"|}, () => 3);

                if (condition)
                {
                    await Step(3).WithId("branch");
                }
                else
                {
                    await Step(4).WithId("branch");
                }

                foreach (var item in items)
                {
                    await Step(item).WithId("item");
                }
            }
        }
        """);

    [Fact]
    public Task BodiesStayOnTheirScheduler() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask RunAsync()
            {
                await Step(1).WithId("step");
                await {|ORLEANS0109:Task.Run(() => { })|};
                var thread = {|ORLEANS0109:new Thread(() => { })|};
                DurableTask.RunInline("effect", () => {|ORLEANS0109:Task.Run(() => 1)|}.Result);
            }

            public async Task OrdinaryAsync() => await Task.Run(() => { });
        }
        """);

    [Fact]
    public Task ConcurrentDurableWorkRunsThroughDurableCombinators() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask RunAsync(int[] items)
            {
                await DurableTask.WhenAll(items.Select(item => Step(item))).WithId("durable");
                await {|ORLEANS0110:Task.WhenAll(items.Select(async item => await Step(item).WithId($"task-{item}")))|};
            }

            public async DurableTask OtherAsync(int[] items)
            {
                await Step(1).WithId("first");
                await Task.WhenAll(items.Select(async item => await FetchAsync()));
            }
        }
        """);

    [Fact]
    public Task CancellationHandlersFilterByIsTaskCancellationRequested() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync()
            {
                try
                {
                    await Step(1).WithId("first");
                }
                {|ORLEANS0112:catch|} (OperationCanceledException)
                {
                }

                try
                {
                    await Step(2).WithId("second");
                }
                catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
                {
                    return 0;
                }

                try
                {
                    await Step(3).WithId("third");
                }
                {|ORLEANS0112:catch|} (OperationCanceledException) when (DurableTask.Current.CancellationToken.IsCancellationRequested)
                {
                    return 0;
                }

                try
                {
                    await Step(4).WithId("fourth");
                }
                catch (Exception)
                {
                    throw;
                }

                try
                {
                    {|ORLEANS0102:await FetchAsync()|};
                }
                catch (OperationCanceledException)
                {
                }

                try
                {
                    await Step(5).WithId("fifth");
                }
                catch (InvalidOperationException)
                {
                }

                return 1;
            }
        }
        """);

    [Fact]
    public Task AsynchronousCodeInBodiesReturnsTasks() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask RunAsync()
            {
                Action handler = {|ORLEANS0113:async () => await Task.Yield()|};
                Func<Task> task = async () => await Task.Yield();
                await Step(1).WithId("step");

                async void {|ORLEANS0113:Fire|}() => await Task.Yield();
            }
        }
        """);

    [Fact]
    public Task RepeatedSignalWaitsDeriveNames() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask RunAsync(int rounds)
            {
                for (var round = 0; round < rounds; round++)
                {
                    await {|ORLEANS0114:Approved.WaitAsync()|};
                    await Approved.For($"round-{round}").WaitAsync();
                }

                await Approved.WaitAsync();
            }
        }
        """);

    [Fact]
    public Task LogsInReplayedCodeUseReplaySafeLoggers() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            private readonly ILogger _plain;
            private readonly ILogger _safe;

            public C(ILogger logger)
            {
                _plain = logger;
                _safe = logger.ReplaySafe();
            }

            public async DurableTask RunAsync(ILogger parameter)
            {
                {|ORLEANS0115:_plain.LogInformation("replayed")|};
                {|ORLEANS0115:parameter.LogInformation("replayed")|};
                _safe.LogInformation("once");
                parameter.ReplaySafe().LogInformation("once");
                await Step(1).WithId("step");
                _plain.LogInformation("tail");
            }
        }
        """);

    [Fact]
    public Task PostRunsInGrainsAndDurableTaskBodies() => AnalyzerVerifier.VerifyAsync("""
        public class CheckoutGrain : Orleans.Grain
        {
            private static DurableTask<int> Step(int value) => DurableTask.FromResult(value);

            public void Checkout() => Step(1).Detached().WithId("order").Post();

            public sealed class Nested
            {
                public void Checkout() => Step(1).Post();
            }
        }

        public class Client
        {
            private static DurableTask<int> Step(int value) => DurableTask.FromResult(value);

            public Task<ScheduledTask<int>> ScheduleAsync() => Step(1).WithId("order").ScheduleAsync();

            public void Start() => {|ORLEANS0116:Step(1).WithId("order").Post()|};

            public async DurableTask RunAsync(int[] items)
            {
                Step(2).Detached().Post();
                await DurableTask.Run(_ => { Step(3).Post(); });
                foreach (var item in items.Where(item => Step(item).Post() is not null))
                {
                }

                async DurableTask LocalAsync()
                {
                    Step(4).Post();
                    await Step(4).WithId("after-post");
                }

                await LocalAsync();
            }

            public static void Helper() => {|ORLEANS0116:Step(5).Post()|};
        }
        """);
}
