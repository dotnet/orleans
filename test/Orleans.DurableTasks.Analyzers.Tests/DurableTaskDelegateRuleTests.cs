using Xunit;

namespace Orleans.DurableTasks.Analyzers.Tests;

/// <summary>Tests the rules for the delegates which DurableTask.Run and DurableTask.RunInline receive.</summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class DurableTaskDelegateRuleTests
{
    private const string Members = """
            private readonly IDurableValue<int> _status = null!;

            private static Task<int> FetchAsync() => Task.FromResult(1);

            private static int Compute(CancellationToken cancellationToken) => 1;
        """;

    [Fact]
    public Task SynchronousStepsRunInline() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync(int[] items)
            {
                var sum = await DurableTask.{|ORLEANS0221:Run|}(_ => items.Sum()).WithId("sum");
                await DurableTask.{|ORLEANS0221:Run|}(_ => { _status.Value = sum; }).WithId("set").Shielded();
                await DurableTask.{|ORLEANS0221:Run|}(async () => { _status.Value = 2; }).WithId("async-without-await");

                await DurableTask.Run(cancellationToken => Compute(cancellationToken)).WithId("token");
                await DurableTask.Run(async () => await FetchAsync()).WithId("awaits");
                await DurableTask.Run(_ => FetchAsync()).WithId("returns-task");
                var reusable = DurableTask.Run(_ => 3);
                await reusable.WithId("reusable");
                await DurableTask.Run(_ => 4).{|ORLEANS0228:Detached|}().WithId("detached");
                var handle = await DurableTask.Run(_ => 5).WithId("scheduled").ScheduleAsync();
                return await handle;
            }

            public async Task<int> ClientAsync() => await DurableTask.Run(_ => 6).WithId("client");
        }
        """);

    [Fact]
    public Task AsynchronousDelegatesReturnTasks() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            private static async void FireAsync() => await FetchAsync();

            public async DurableTask RunAsync()
            {
                DurableTask.RunInline("cast", {|ORLEANS0222:(Action)(async () => await FetchAsync())|});
                await DurableTask.Run({|ORLEANS0222:(Action<CancellationToken>)(async _ => await FetchAsync())|}).WithId("run");
                DurableTask.RunInline("method", {|ORLEANS0222:FireAsync|});
                Action handler = {|ORLEANS0213:async () => await Task.Yield()|};
                DurableTask.RunInline("alias", {|ORLEANS0222:handler|});

                DurableTask.RunInline("synchronous", () => { });
                await DurableTask.Run(async () => await FetchAsync()).WithId("task");
            }

                public static DurableTask Define() => DurableTask.Run({|ORLEANS0222:(Action<CancellationToken>)(async _ => await FetchAsync())|});
        }
        """);

    [Fact]
    public Task RealOverloadsBindTheDelegatesWhichTheRulesInspect() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            private static ValueTask<int> FetchValueAsync() => new(1);

            public async DurableTask<int> RunAsync(int seed)
            {
                // ValueTask steps and steps with state run asynchronous work, so they're steps.
                var value = await DurableTask.Run(async () => await FetchValueAsync()).WithId("value-task");
                await DurableTask.Run(async () => { await FetchValueAsync(); }).WithId("value-task-action");
                var stateful = await DurableTask.Run(static async (state, _) => await FetchAsync() + state, seed).WithId("state");

                // A synchronous delegate with state is a step which runs no asynchronous work.
                var synchronous = await DurableTask.{|ORLEANS0221:Run|}(static (state, _) => state + 1, seed).WithId("synchronous-state");
                return value + stateful + synchronous;
            }
        }
        """);
}
