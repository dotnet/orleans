using System.Threading;
using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace UnitTests.Runtime;

public class DefaultExecutionContextTests
{
    [Fact, TestCategory("BVT")]
    public void InstanceDoesNotContainAmbientState() => AssertDoesNotContainAmbientState(DefaultExecutionContext.Instance);

    [Fact, TestCategory("BVT")]
    public void FallbackDoesNotContainAmbientState() => AssertDoesNotContainAmbientState(DefaultExecutionContext.CaptureDefault());

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
