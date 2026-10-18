using Xunit;

namespace Orleans.DurableTasks.Analyzers.Tests;

/// <summary>Tests the rule which names unnamed durable operations whose generated identifiers can shift, and its code fix.</summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class DurableTaskIdentityRuleTests
{
    private const string Members = """
            private static readonly DurableSignal<string> Approved = new("approved");

            private static DurableTask<int> Step(int value) => DurableTask.FromResult(value);

            private static DurableTask ChargeAsync() => DurableTask.FromResult(0);
        """;

    [Fact]
    public Task OperationsInLoopsConditionsOrAfterUnnamedSiblingsAreNamed() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask RunAsync(bool condition, int[] items)
            {
                await Step(1);
                await {|ORLEANS0120:Step(2)|};
                if (condition)
                {
                    await {|ORLEANS0120:Step(3)|};
                }

                foreach (var item in items)
                {
                    await {|ORLEANS0120:Step(item)|};
                    await Step(item).WithId("item");
                }

                var chosen = condition ? await {|ORLEANS0120:Step(4)|} : 0;
                DurableTask.{|ORLEANS0120:RunInline|}(() => chosen);
                DurableTask.RunInline("named", () => chosen);
                {|ORLEANS0120:Step(5)|}.Detached().Post();
                await Approved.WaitAsync();

                // Awaited combinators count their own identifiers, and their inputs are identified by index.
                await DurableTask.WhenAll(items.Select(item => Step(item)));
                await {|ORLEANS0120:DurableTask.WhenAny(items.Select(item => Step(item)))|};
                await Approved.TryWaitAsync(DateTimeOffset.MaxValue).WithId("approval");
            }

            public async DurableTask SingleAsync(bool condition)
            {
                if (condition)
                {
                    return;
                }

                await ChargeAsync();
                await DurableTask.Delay(TimeSpan.FromSeconds(1)).WithId("delay");
                await DurableTask.WhenAll(new[] { Step(1) });
            }

            public async DurableTask StepsAsync(int[] items)
            {
                await Step(1);

                // A step is a body with identifiers of its own.
                await DurableTask.Run(async () =>
                {
                    await Step(2);
                }).WithId("step");

                // A query can run its lambda any number of times.
                foreach (var _ in items.Select(item => {|ORLEANS0120:Step(item)|}.Post()))
                {
                }
            }

            public async Task ClientAsync(bool condition)
            {
                if (condition)
                {
                    await Step(1);
                }

                await Step(2);
            }
        }
        """);

    [Fact]
    public Task CodeFixNamesOperationsAfterTheirMethods() => AnalyzerVerifier.VerifyCodeFixAsync(
        new DurableTaskIdentityCodeFix(),
        $$"""
        public class C
        {
        {{Members}}
            public async DurableTask RunAsync(int[] items)
            {
                await Step(1).WithId("step");
                foreach (var item in items)
                {
                    await {|ORLEANS0120:Step(item)|};
                }

                await {|ORLEANS0120:ChargeAsync()|}.Shielded();
                await {|ORLEANS0120:ChargeAsync()|};
                DurableTask.{|ORLEANS0120:RunInline|}(() => 1);
            }
        }
        """,
        $$"""
        public class C
        {
        {{Members}}
            public async DurableTask RunAsync(int[] items)
            {
                await Step(1).WithId("step");
                foreach (var item in items)
                {
                    await Step(item).WithId("step-2");
                }

                await ChargeAsync().WithId("charge").Shielded();
                await ChargeAsync().WithId("charge-2");
                DurableTask.RunInline(() => 1);
            }
        }
        """);
}
