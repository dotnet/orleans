using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Runtime")]
public class DefaultExecutionContextTests
{
    [Fact]
    public void InstanceDoesNotContainAmbientState() => AssertDoesNotContainAmbientState(DefaultExecutionContext.Instance);

    [Fact]
    public void FallbackDoesNotContainAmbientState() => AssertDoesNotContainAmbientState(DefaultExecutionContext.CaptureDefault());

    [Fact]
    public async Task InstanceSupportsConcurrentExecution()
    {
        var tasks = new Task[Math.Max(4, Environment.ProcessorCount)];
        for (var i = 0; i < tasks.Length; i++)
        {
            var expected = new object();
            tasks[i] = Task.Run(
                () =>
                {
                    var ambientState = new AsyncLocal<object?> { Value = expected };
                    object? observed = expected;

                    ExecutionContext.Run(
                        DefaultExecutionContext.Instance,
                        _ => observed = ambientState.Value,
                        null);

                    Assert.Null(observed);
                    Assert.Same(expected, ambientState.Value);
                },
                TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(tasks);
    }

    private static void AssertDoesNotContainAmbientState(ExecutionContext executionContext)
    {
        var ambientState = new AsyncLocal<object?>();
        var expected = new object();
        ambientState.Value = expected;
        object? observed = expected;
        var flowSuppressed = true;

        try
        {
            ExecutionContext.Run(
                executionContext,
                _ =>
                {
                    observed = ambientState.Value;
                    flowSuppressed = ExecutionContext.IsFlowSuppressed();
                },
                null);

            Assert.Null(observed);
            Assert.False(flowSuppressed);
            Assert.Same(expected, ambientState.Value);
        }
        finally
        {
            ambientState.Value = null;
        }
    }
}
