using System;
using System.Threading;
using Orleans.Runtime;
using Orleans.Transactions.Diagnostics;

namespace Orleans.Transactions.Tests;

public static class BankTransferDiagnosticFaults
{
    public static StorageWriteCompletedFaultScope ThrowOnStorageWriteCompleted(IAddressable target, string stateName = "balance")
    {
        return new StorageWriteCompletedFaultScope(target.GetGrainId(), stateName);
    }
}

public sealed class StorageWriteCompletedFaultScope : IDisposable
{
    private readonly IDisposable subscription;
    private readonly GrainId targetGrainId;
    private readonly string stateName;
    private int shouldThrow = 1;
    private int observedCount;

    internal StorageWriteCompletedFaultScope(GrainId targetGrainId, string stateName)
    {
        this.targetGrainId = targetGrainId;
        this.stateName = stateName;
        this.subscription = TransactionQueueEvents.AllEvents.Subscribe(new Observer(this));
    }

    public int ObservedCount => Volatile.Read(ref this.observedCount);

    public bool FaultInjected => Volatile.Read(ref this.shouldThrow) == 0;

    public void Dispose()
    {
        this.subscription.Dispose();
    }

    private void OnStorageWriteCompleted(TransactionQueueEvents.StorageWriteCompleted evt)
    {
        if (evt.Resource.Name != this.stateName || evt.Resource.Reference.GrainId != this.targetGrainId || evt.CommitCount == 0)
        {
            return;
        }

        Interlocked.Increment(ref this.observedCount);
        if (Interlocked.Exchange(ref this.shouldThrow, 0) == 1)
        {
            throw new InvalidOperationException(
                $"Transaction queue exception thrown after storage write completed for {evt.Resource}, batch size {evt.BatchSize}, etag {evt.ETag}");
        }
    }

    private sealed class Observer(StorageWriteCompletedFaultScope scope) : IObserver<TransactionQueueEvents.TransactionQueueEvent>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(TransactionQueueEvents.TransactionQueueEvent value)
        {
            if (value is TransactionQueueEvents.StorageWriteCompleted storageWriteCompleted)
            {
                scope.OnStorageWriteCompleted(storageWriteCompleted);
            }
        }
    }
}
