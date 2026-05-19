namespace Orleans.Transactions.State;

internal interface ITransactionQueueStorageEventHandler
{
    void OnStorageWriteCompleted();
}
