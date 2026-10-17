using Xunit;

namespace Orleans.DurableTasks.Analyzers.Tests;

/// <summary>Tests the rule and code fix for cleanup which passes the already-canceled token after task cancellation.</summary>
[TestCategory("BVT"), TestCategory("Analyzer")]
public class DurableTaskCleanupTests
{
    private const string Members = """
            private static DurableTask<int> Step(int value) => DurableTask.FromResult(value);

            private static Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        """;

    [Fact]
    public Task CleanupAfterTaskCancellationPassesTheAttemptToken() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync(DurableTaskContext context, bool compensate)
            {
                try
                {
                    await Step(1).WithId("first");
                }
                catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
                {
                    await ReleaseAsync({|ORLEANS0117:DurableTask.Current.CancellationToken|});
                    await ReleaseAsync(DurableTask.Current.AttemptCancellationToken);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource({|ORLEANS0117:context.CancellationToken|});
                    try
                    {
                        await ReleaseAsync({|ORLEANS0117:context.CancellationToken|});
                    }
                    finally
                    {
                        await ReleaseAsync({|ORLEANS0117:context.CancellationToken|});
                    }

                    return 0;
                }

                try
                {
                    await Step(2).WithId("second");
                }
                catch (OperationCanceledException) when (compensate && (context.IsTaskCancellationRequested && context.UtcNow.Year > 2000))
                {
                    await ReleaseAsync({|ORLEANS0117:context.CancellationToken|});
                    throw;
                }

                if (context.IsTaskCancellationRequested)
                {
                    await ReleaseAsync({|ORLEANS0117:context.CancellationToken|});
                }

                if (compensate && DurableTask.Current.IsTaskCancellationRequested)
                    await ReleaseAsync({|ORLEANS0117:DurableTask.Current.CancellationToken|});

                var release = DurableTask.Current.IsTaskCancellationRequested
                    ? ReleaseAsync({|ORLEANS0117:context.CancellationToken|})
                    : ReleaseAsync(context.CancellationToken);
                await release;
                return 1;
            }
        }
        """);

    [Fact]
    public Task OrdinaryCodePassesTheCancellationToken() => AnalyzerVerifier.VerifyAsync($$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync(DurableTaskContext context, bool compensate)
            {
                try
                {
                    await Step(1).WithId("first");
                }
                {|ORLEANS0112:catch|} (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    await ReleaseAsync(context.CancellationToken);
                }
                catch (OperationCanceledException) when (!context.IsTaskCancellationRequested)
                {
                    await ReleaseAsync(context.CancellationToken);
                }
                catch (OperationCanceledException) when (compensate || context.IsTaskCancellationRequested)
                {
                    await ReleaseAsync(context.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    await ReleaseAsync(context.CancellationToken);
                    throw;
                }

                if (!context.IsTaskCancellationRequested)
                {
                    await ReleaseAsync(context.CancellationToken);
                }
                else
                {
                    await ReleaseAsync(context.AttemptCancellationToken);
                }

                if (context.IsTaskCancellationRequested)
                {
                    Func<Task> release = () => ReleaseAsync(context.CancellationToken);
                    await release();
                    await LocalAsync();

                    Task LocalAsync() => ReleaseAsync(context.CancellationToken);
                }

                return 0;
            }
        }
        """);

    [Fact]
    public Task CodeFixPassesTheAttemptToken() => AnalyzerVerifier.VerifyCodeFixAsync(
        new DurableTaskCleanupCodeFix(),
        $$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync(DurableTaskContext context)
            {
                try
                {
                    await Step(1).WithId("first");
                }
                catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
                {
                    await ReleaseAsync({|ORLEANS0117:DurableTask.Current.CancellationToken|});
                    await ReleaseAsync({|ORLEANS0117:context
                        .CancellationToken|});
                    return 0;
                }

                return 1;
            }
        }
        """,
        $$"""
        public class C
        {
        {{Members}}
            public async DurableTask<int> RunAsync(DurableTaskContext context)
            {
                try
                {
                    await Step(1).WithId("first");
                }
                catch (OperationCanceledException) when (DurableTask.Current.IsTaskCancellationRequested)
                {
                    await ReleaseAsync(DurableTask.Current.AttemptCancellationToken);
                    await ReleaseAsync(context
                        .AttemptCancellationToken);
                    return 0;
                }

                return 1;
            }
        }
        """);
}
