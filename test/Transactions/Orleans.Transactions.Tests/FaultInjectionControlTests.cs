using System;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Theory]
    [InlineData(FaultInjectionType.ExceptionBeforeStore)]
    [InlineData(FaultInjectionType.ExceptionAfterStore)]
    [InlineData(FaultInjectionType.GenericExceptionAfterStore)]
    public void ScopedStorageFaultIgnoresUnrelatedBatches(FaultInjectionType injectionType)
    {
        var targetTransactionId = Guid.NewGuid();
        var injector = new SimpleAzureStorageExceptionInjector(
            NullLogger<SimpleAzureStorageExceptionInjector>.Instance);
        var scopedInjector = Assert.IsAssignableFrom<ITransactionScopedFaultInjector>(injector);
        scopedInjector.Arm(targetTransactionId, injectionType, requireTransactionMatch: true);

        var unrelatedTransactionIds = ImmutableArray.Create(Guid.NewGuid());
        scopedInjector.BeforeStore(unrelatedTransactionIds);
        scopedInjector.AfterStore(unrelatedTransactionIds);

        var targetTransactionIds = ImmutableArray.Create(targetTransactionId);
        AssertFaultInjected(scopedInjector, injectionType, targetTransactionIds);

        scopedInjector.BeforeStore(targetTransactionIds);
        scopedInjector.AfterStore(targetTransactionIds);
    }

    [Theory]
    [InlineData(FaultInjectionType.ExceptionBeforeStore)]
    [InlineData(FaultInjectionType.ExceptionAfterStore)]
    [InlineData(FaultInjectionType.GenericExceptionAfterStore)]
    public void UnscopedStorageFaultInjectsIntoUnidentifiedBatch(FaultInjectionType injectionType)
    {
        var injector = new SimpleAzureStorageExceptionInjector(
            NullLogger<SimpleAzureStorageExceptionInjector>.Instance);
        var scopedInjector = Assert.IsAssignableFrom<ITransactionScopedFaultInjector>(injector);
        scopedInjector.Arm(Guid.NewGuid(), injectionType, requireTransactionMatch: false);

        AssertFaultInjected(scopedInjector, injectionType, ImmutableArray<Guid>.Empty);

        scopedInjector.BeforeStore(ImmutableArray<Guid>.Empty);
        scopedInjector.AfterStore(ImmutableArray<Guid>.Empty);
    }

    private static void AssertFaultInjected(
        ITransactionScopedFaultInjector faultInjector,
        FaultInjectionType injectionType,
        ImmutableArray<Guid> transactionIds)
    {
        if (injectionType == FaultInjectionType.ExceptionBeforeStore)
        {
            Assert.Throws<SimpleAzureStorageException>(() => faultInjector.BeforeStore(transactionIds));
            return;
        }

        faultInjector.BeforeStore(transactionIds);
        if (injectionType == FaultInjectionType.ExceptionAfterStore)
        {
            Assert.Throws<SimpleAzureStorageException>(() => faultInjector.AfterStore(transactionIds));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => faultInjector.AfterStore(transactionIds));
        }
    }
}
