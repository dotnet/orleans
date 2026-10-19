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
    public Task DetachedTasksAreDurableGrainCalls() => AnalyzerVerifier.VerifyAsync($$"""
        public interface IShipping
        {
            DurableTask<int> ShipAsync(int order);
        }

        public class C(IShipping shipping)
        {
        {{Members}}
            public async DurableTask<int> RunAsync()
            {
                await DurableTask.Run(static _ => 1).{|ORLEANS0228:Detached|}();
                _ = LocalStep(2).WithId("local").{|ORLEANS0228:Detached|}().Post();
                _ = await DurableTask.WhenAll([Step(1), Step(2)]).{|ORLEANS0228:Detached|}("batch").ScheduleAsync();

                // A durable grain call starts as a root task on its grain, in any configuration order.
                _ = shipping.ShipAsync(1).WithId("ship-1").Detached().Post();
                _ = shipping.ShipAsync(2).WithId("ship").Detached().Post();
                return await shipping.ShipAsync(3).Detached("ship-3");
            }
        }
        """);

    [Fact]
    public Task RunInlineEffectsReturnTheirOutputsInsteadOfChangingCapturedObjects() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public sealed class Order
            {
                public int Total { get; set; }
            }

            public async DurableTask<int> RunAsync(Order order)
            {
                var reserved = new List<int>();
                DurableTask.RunInline("reserve", () => { {|ORLEANS0204:reserved.Add(1)|}; });
                DurableTask.RunInline("total", () => { {|ORLEANS0204:order.Total = 2|}; });
                var returned = DurableTask.RunInline("return", () =>
                {
                    var lines = new List<int>();
                    lines.Add(3);
                    return lines.Count;
                });
                await Step(reserved.Count + returned).WithId("step");
                return order.Total;
            }
        }
        """);

    [Fact]
    public Task DurableOperationsRunOutsideEffects() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            private static readonly DurableVersion Carrier = new("carrier", 0, 1);

            public async DurableTask<int> RunAsync()
            {
                DurableTask.RunInline("post", () => { {|ORLEANS0227:Step(1).WithId("child").Post()|}; });
                var version = DurableTask.RunInline("version", () => {|ORLEANS0227:DurableTask.GetVersion("change", 0, 1)|});
                var carrier = DurableTask.RunInline("carrier", () => {|ORLEANS0227:Carrier.IsAtLeast(1)|});

                // A nested effect runs as part of its enclosing effect.
                var nested = DurableTask.RunInline("nested", () => DurableTask.RunInline("inner", () => 1));
                return await Step(version + nested + (carrier ? 1 : 0)).WithId("step");
            }
        }
        """);

    [Fact]
    public Task BodiesAwaitTasksInsteadOfPollingThem() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<DurableTaskStatus> RunAsync()
            {
                var child = await Step(1).WithId("child").ScheduleAsync();
                while (!await {|ORLEANS0225:child.IsCompletedAsync()|})
                {
                    await DurableTask.Delay(TimeSpan.FromMinutes(1)).WithId("wait");
                }

                _ = await {|ORLEANS0225:child.GetResponseAsync(new PollingOptions())|};
                _ = await child.GetResponseAsync();
                return await {|ORLEANS0225:child.GetStatusAsync()|};
            }

            // A client polls, because it has no execution to replay.
            public async Task<bool> ClientAsync(ScheduledTask task) => await task.IsCompletedAsync();
        }
        """);

    [Fact]
    public Task BodiesContinueOnTheirScheduler() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync()
            {
                var step = await DurableTask.Run(async () => await {|ORLEANS0226:FetchAsync().ConfigureAwait(false)|});
                var recorded = await Step(1).WithId("step");

                // The tail runs once, after the last durable operation, and continues on the scheduler too.
                var fetched = await {|ORLEANS0226:FetchAsync().ConfigureAwait(false)|};
                await {|ORLEANS0226:Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing)|};
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
                return fetched + step + recorded + await FetchAsync().ConfigureAwait(true);
            }

            // Code outside durable task bodies, such as a client, continues where it likes.
            public async Task<int> ClientAsync() => await FetchAsync().ConfigureAwait(false);
        }
        """);

    [Fact]
    public Task RunInlineEffectsReturnTheirOutputs() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync()
            {
                var captured = 0;
                DurableTask.RunInline("assign", () => { {|ORLEANS0204:captured = 1|}; });
                DurableTask.RunInline("increment", () => { {|ORLEANS0204:captured++|}; });
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
                DurableTask.RunInline(() => { {|ORLEANS0204:captured = 1|}; });
                DurableTask.{|ORLEANS0220:RunInline|}(() => { {|ORLEANS0204:captured++|}; });
                var returned = DurableTask.{|ORLEANS0220:RunInline|}(() =>
                {
                    var local = 2;
                    local++;
                    _status.Value = local;
                    return local;
                });
                DurableTask.{|ORLEANS0220:RunInline|}(() => {|ORLEANS0209:Task.Run(() => 1)|}.Result);

                // Unnamed effects take generated identifiers, so neither a result which contains '$' nor a repeated call
                // site is a name.
                DurableTask.{|ORLEANS0220:RunInline|}(() => "$value");
                DurableTask.{|ORLEANS0220:RunInline|}(() => 3);
                DurableTask.{|ORLEANS0220:RunInline|}(() => 3);
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
                {|ORLEANS0205:Step(1);|}
                {|ORLEANS0205:_ = LocalStep(2);|}
                {|ORLEANS0205:Step(3).WithId("unobserved");|}
                {|ORLEANS0205:Step(3).Detached().Shielded();|}
                await Step(4).WithId("awaited");
                Step(5).Detached().Post();
                _ = await {|ORLEANS0219:Step(5).WithId("discarded").ScheduleAsync()|};
                var definition = Step(6);
                await definition.WithId("later");
            }
        }
        """);

    [Fact]
    public Task DefinitionsWhichLocalsHoldAreAwaitedScheduledOrPosted() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            private static Task SaveAsync() => Task.CompletedTask;

            private static void Consume(DurableTask task) { }

            public async DurableTask RunAsync(bool condition)
            {
                var {|ORLEANS0205:write = DurableTask.Run(SaveAsync)|};
                var {|ORLEANS0205:named = Step(1).WithId("named")|};
                var {|ORLEANS0205:original = Step(2)|};
                var alias = original;
                var configured = alias.WithId("alias");

                var awaited = Step(3);
                await awaited.WithId("awaited");
                var passed = Step(4);
                Consume(passed);
                var scheduled = Step(5);
                await (await scheduled.WithId("scheduled").ScheduleAsync());
                var posted = Step(6);
                await posted.WithId("posted").Post();
                var conditional = Step(7);
                if (condition)
                {
                    await conditional.WithId("conditional");
                }

                var combined = Step(8);
                await DurableTask.WhenAll(new[] { combined }).WithId("combined");
                var captured = Step(9);
                await DurableTask.Run(async () => await captured.WithId("captured")).WithId("capture");
                var reassigned = Step(10);
                reassigned = Step(11);
                return;
            }

            public static DurableTask Create()
            {
                var returned = Step(12);
                return returned;
            }
        }
        """);

    [Fact]
    public Task HandlesOfScheduledChildrenAreObserved() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            private static void Track(ScheduledTask task) { }

            public async DurableTask RunAsync()
            {
                await {|ORLEANS0219:Step(1).WithId("discarded").ScheduleAsync()|};
                var unobserved = await {|ORLEANS0219:Step(2).WithId("unobserved").ScheduleAsync()|};
                var identified = await {|ORLEANS0219:Step(3).WithId("identified").ScheduleAsync()|};
                _ = identified.Id;

                var awaited = await Step(4).WithId("awaited").ScheduleAsync();
                await awaited;
                var combined = await Step(5).WithId("combined").ScheduleAsync();
                await ScheduledTask.WhenAll(new[] { combined });
                var passed = await Step(6).WithId("passed").ScheduleAsync();
                Track(passed);
                await Step(7).WithId("detached").Detached().ScheduleAsync();
            }

            public async DurableTask<ScheduledTask> ReturnAsync() => await Step(8).WithId("returned").ScheduleAsync();

            public async Task ClientAsync() => await Step(9).WithId("client").ScheduleAsync();
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
                await {|ORLEANS0206:once|}.WithId("second");

                var outside = LocalStep(2);
                foreach (var item in items)
                {
                    await {|ORLEANS0206:outside|}.WithId($"loop-{item}");
                }

                foreach (var item in items)
                {
                    var inside = LocalStep(item);
                    await inside.WithId($"inside-{item}");
                }

                var reusable = DurableTask.Run(_ => 3);
                await reusable.WithId("run-1");
                await reusable.WithId("run-2");

                var configured = LocalStep(3).Shielded();
                configured.Post();
                {|ORLEANS0206:configured|}.Post();
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
                await Step(1).WithId({|ORLEANS0207:"line$2"|});
                DurableTask.RunInline({|ORLEANS0207:"$effect"|}, () => 1);
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
            private static int Record() => DurableTask.RunInline({|ORLEANS0207:"record$1"|}, () => 1);
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
                await Step(2).WithId({|ORLEANS0208:"pay"|});
                DurableTask.RunInline({|ORLEANS0208:"pay"|}, () => 3);

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
                await {|ORLEANS0209:Task.Run(() => { })|};
                var thread = {|ORLEANS0209:new Thread(() => { })|};
                DurableTask.RunInline("effect", () => {|ORLEANS0209:Task.Run(() => 1)|}.Result);
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
                await {|ORLEANS0210:Task.WhenAll(items.Select(async item => await Step(item).WithId($"task-{item}")))|};
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
                {|ORLEANS0212:catch|} (OperationCanceledException)
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
                {|ORLEANS0212:catch|} (OperationCanceledException) when (DurableTask.Current.CancellationToken.IsCancellationRequested)
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
                    {|ORLEANS0202:await FetchAsync()|};
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
                Action handler = {|ORLEANS0213:async () => await Task.Yield()|};
                Func<Task> task = async () => await Task.Yield();
                await Step(1).WithId("step");

                async void {|ORLEANS0213:Fire|}() => await Task.Yield();
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
                    await {|ORLEANS0214:Approved.WaitAsync()|};
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
                {|ORLEANS0215:_plain.LogInformation("replayed")|};
                {|ORLEANS0215:parameter.LogInformation("replayed")|};
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

            public void Start() => {|ORLEANS0216:Step(1).WithId("order").Post()|};

            public async DurableTask RunAsync(int[] items)
            {
                Step(2).Detached().Post();
                await DurableTask.{|ORLEANS0221:Run|}(_ => { Step(3).WithId("in-step").Post(); }).WithId("step");
                foreach (var item in items.Where(item => Step(item).WithId("filter").Post() is not null))
                {
                }

                async DurableTask LocalAsync()
                {
                    Step(4).Post();
                    await Step(4).WithId("after-post");
                }

                await LocalAsync().WithId("local");
            }

            public static void Helper() => {|ORLEANS0216:Step(5).Post()|};
        }
        """);
}
