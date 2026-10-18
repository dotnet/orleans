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
                var sum = await DurableTask.{|ORLEANS0121:Run|}(_ => items.Sum()).WithId("sum");
                await DurableTask.{|ORLEANS0121:Run|}(_ => { _status.Value = sum; }).WithId("set").Shielded();
                await DurableTask.{|ORLEANS0121:Run|}(async () => { _status.Value = 2; }).WithId("async-without-await");

                await DurableTask.Run(cancellationToken => Compute(cancellationToken)).WithId("token");
                await DurableTask.Run(async () => await FetchAsync()).WithId("awaits");
                await DurableTask.Run(_ => FetchAsync()).WithId("returns-task");
                var reusable = DurableTask.Run(_ => 3);
                await reusable.WithId("reusable");
                await DurableTask.Run(_ => 4).Detached().WithId("detached");
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
                DurableTask.RunInline("cast", {|ORLEANS0122:(Action)(async () => await FetchAsync())|});
                await DurableTask.Run({|ORLEANS0122:(Action<CancellationToken>)(async _ => await FetchAsync())|}).WithId("run");
                DurableTask.RunInline("method", {|ORLEANS0122:FireAsync|});
                Action handler = {|ORLEANS0113:async () => await Task.Yield()|};
                DurableTask.RunInline("alias", {|ORLEANS0122:handler|});

                DurableTask.RunInline("synchronous", () => { });
                await DurableTask.Run(async () => await FetchAsync()).WithId("task");
            }

            public static DurableTask Define() => DurableTask.Run({|ORLEANS0122:(Action<CancellationToken>)(async _ => await FetchAsync())|});
        }
        """);
}
