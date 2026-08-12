using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.DynamoDB.TransactionalState;

namespace Orleans.Transactions.TestKit.Base.FaultInjection.ControlledInjection;

/// <summary>
/// Wraps DynamoDB transactional state storage with transaction fault injection.
/// </summary>
/// <typeparam name="TState">The state type.</typeparam>
public class FaultInjectionDynamoDBTransactionStateStorage<TState> : ITransactionalStateStorage<TState>
    where TState : class, new()
{
    private readonly DynamoDBTransactionalStateStorage<TState> stateStorage;
    private readonly ITransactionFaultInjector faultInjector;

    /// <summary>
    /// Initializes a new instance of the <see cref="FaultInjectionDynamoDBTransactionStateStorage{TState}"/> class.
    /// </summary>
    /// <param name="faultInjector">The transaction fault injector.</param>
    /// <param name="dynamodbStateStorage">The wrapped DynamoDB transactional state storage.</param>
    public FaultInjectionDynamoDBTransactionStateStorage(
        ITransactionFaultInjector faultInjector,
        DynamoDBTransactionalStateStorage<TState> dynamodbStateStorage)
    {
        this.faultInjector = faultInjector;
        this.stateStorage = dynamodbStateStorage;
    }

    /// <inheritdoc />
    public Task<TransactionalStorageLoadResponse<TState>> Load()
    {
        return this.stateStorage.Load();
    }

    /// <inheritdoc />
    public async Task<string> Store(

        string? expectedETag,
        TransactionalStateMetaData metadata,

        // a list of transactions to prepare.
        List<PendingTransactionState<TState>>? statesToPrepare,

        // if non-null, commit all pending transaction up to and including this sequence number.
        long? commitUpTo,

        // if non-null, abort all pending transactions with sequence numbers strictly larger than this one.
        long? abortAfter
    )
    {
        var transactionIds = ControlledTransactionFaultInjectorExtensions.GetTransactionIds(
            metadata,
            statesToPrepare);
        faultInjector.BeforeStore(transactionIds);
        var result = await this.stateStorage.Store(expectedETag, metadata, statesToPrepare, commitUpTo, abortAfter);
        faultInjector.AfterStore(transactionIds);
        return result;
    }
}

/// <summary>
/// Creates fault-injecting DynamoDB transactional state storage instances.
/// </summary>
public class FaultInjectionDynamoDBTransactionStateStorageFactory : ITransactionalStateStorageFactory,
    ILifecycleParticipant<ISiloLifecycle>
{
    private readonly DynamoDBTransactionalStateStorageFactory factory;

    /// <summary>
    /// Creates a fault-injecting transactional state storage factory.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <param name="name">The provider name.</param>
    /// <returns>The transactional state storage factory.</returns>
    public static ITransactionalStateStorageFactory Create(IServiceProvider services, string name)
    {
        var optionsMonitor = services.GetRequiredService<IOptionsMonitor<DynamoDBTransactionalStorageOptions>>();
        var dynamodbFactory = ActivatorUtilities.CreateInstance<DynamoDBTransactionalStateStorageFactory>(services, name, optionsMonitor.Get(name));
        return new FaultInjectionDynamoDBTransactionStateStorageFactory(dynamodbFactory);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FaultInjectionDynamoDBTransactionStateStorageFactory"/> class.
    /// </summary>
    /// <param name="factory">The wrapped DynamoDB transactional state storage factory.</param>
    public FaultInjectionDynamoDBTransactionStateStorageFactory(
        DynamoDBTransactionalStateStorageFactory factory)
    {
        this.factory = factory;
    }

    /// <inheritdoc />
    public ITransactionalStateStorage<TState> Create<TState>(string stateName, IGrainContext context) where TState : class, new()
    {
        var dynamodbStateStorage = this.factory.Create<TState>(stateName, context);
        return ActivatorUtilities.CreateInstance<FaultInjectionDynamoDBTransactionStateStorage<TState>>(
            context.ActivationServices, dynamodbStateStorage);
    }

    /// <inheritdoc />
    public void Participate(ISiloLifecycle lifecycle)
    {
        this.factory.Participate(lifecycle);
    }
}
