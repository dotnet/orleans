using Orleans.Transactions.TestKit;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestCategory("BVT"), TestCategory("Transactions")]
public class FaultInjectionControlTests
{
    [Theory]
    [InlineData(TransactionFaultInjectPhase.BeforePrepare, FaultInjectionType.ExceptionBeforeStore)]
    [InlineData(TransactionFaultInjectPhase.BeforeConfirm, FaultInjectionType.ExceptionAfterStore)]
    [InlineData(TransactionFaultInjectPhase.AfterPrepare, FaultInjectionType.Deactivation)]
    public void MatchingPhaseIsConsumedOnce(
        TransactionFaultInjectPhase phase,
        FaultInjectionType injectionType)
    {
        var control = new FaultInjectionControl
        {
            FaultInjectionPhase = phase,
            FaultInjectionType = injectionType,
        };

        Assert.True(control.TryConsume(phase, out var consumedType));
        Assert.Equal(injectionType, consumedType);
        Assert.Equal(TransactionFaultInjectPhase.None, control.FaultInjectionPhase);
        Assert.Equal(FaultInjectionType.None, control.FaultInjectionType);
        Assert.False(control.TryConsume(phase, out consumedType));
        Assert.Equal(FaultInjectionType.None, consumedType);
    }

    [Fact]
    public void NonMatchingPhaseDoesNotConsumeFault()
    {
        var control = new FaultInjectionControl
        {
            FaultInjectionPhase = TransactionFaultInjectPhase.BeforePrepareAndCommit,
            FaultInjectionType = FaultInjectionType.ExceptionAfterStore,
        };

        Assert.False(control.TryConsume(TransactionFaultInjectPhase.BeforePrepare, out var consumedType));
        Assert.Equal(FaultInjectionType.None, consumedType);
        Assert.Equal(TransactionFaultInjectPhase.BeforePrepareAndCommit, control.FaultInjectionPhase);
        Assert.Equal(FaultInjectionType.ExceptionAfterStore, control.FaultInjectionType);
    }
}
