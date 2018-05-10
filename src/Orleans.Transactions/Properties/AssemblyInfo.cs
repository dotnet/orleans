using Orleans.CodeGeneration;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions;
using Orleans.Transactions.Abstractions.Extensions;

[assembly: GenerateSerializer(typeof(TransactionalExtensionExtensions.TransactionalResourceExtensionWrapper))]
[assembly: GenerateSerializer(typeof(TransactionalStateRecord<>))]
[assembly: GenerateSerializer(typeof(PendingTransactionState<>))]
[assembly: GenerateSerializer(typeof(TransactionalStorageLoadResponse<>))]
