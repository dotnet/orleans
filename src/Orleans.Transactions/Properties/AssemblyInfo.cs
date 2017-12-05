using Orleans.CodeGeneration;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions;

[assembly: GenerateSerializer(typeof(TransactionalExtensionExtensions.TransactionalResourceExtensionWrapper))]
[assembly: GenerateSerializer(typeof(CommitRecord))]
[assembly: GenerateSerializer(typeof(TransactionalStateRecord<>))]
[assembly: GenerateSerializer(typeof(PendingTransactionState<>))]
[assembly: GenerateSerializer(typeof(TransactionalStorageLoadResponse<>))]
