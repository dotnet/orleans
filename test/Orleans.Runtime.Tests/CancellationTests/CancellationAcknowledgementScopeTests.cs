using Orleans.Runtime;
using Xunit;

namespace Tester;

[TestSuite("BVT"), TestProvider("None"), TestCategory("BVT")]
public class CancellationAcknowledgementScopeTests
{
    [Fact]
    public void NestedScopesRestorePreviousPolicy()
    {
        AssertPolicy(false);
        using (CancellationAcknowledgementScope.Enter())
        {
            AssertPolicy(true);
            using (CancellationAcknowledgementScope.Enter())
            {
                AssertPolicy(true);
                using (var captured = CancellationAcknowledgementScope.Capture())
                {
                    Assert.True(captured.WaitForAcknowledgement);
                    AssertPolicy(false);
                }

                AssertPolicy(true);
            }

            AssertPolicy(true);
        }

        AssertPolicy(false);
    }

    [Fact]
    public void SynchronousThrowRestoresPreviousPolicy()
    {
        using (CancellationAcknowledgementScope.Enter())
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var nested = CancellationAcknowledgementScope.Enter();
                using var captured = CancellationAcknowledgementScope.Capture();
                AssertPolicy(false);
                throw new InvalidOperationException("Invocation failed synchronously");
            });
            AssertPolicy(true);
        }

        AssertPolicy(false);
    }

    [Fact]
    public async Task ExecutionContextRetainsItsOwnPolicyAcrossAwait()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task scopedWork;
        using (CancellationAcknowledgementScope.Enter())
        {
            scopedWork = Task.Run(async () =>
            {
                await release.Task;
                AssertPolicy(true);
                using var captured = CancellationAcknowledgementScope.Capture();
                await Task.Yield();
                AssertPolicy(false);
            }, TestContext.Current.CancellationToken);
        }

        AssertPolicy(false);
        release.SetResult();
        await scopedWork.WaitAsync(TestContext.Current.CancellationToken);
        AssertPolicy(false);
    }

    private static void AssertPolicy(bool expected)
    {
        using var captured = CancellationAcknowledgementScope.Capture();
        Assert.Equal(expected, captured.WaitForAcknowledgement);
    }
}
