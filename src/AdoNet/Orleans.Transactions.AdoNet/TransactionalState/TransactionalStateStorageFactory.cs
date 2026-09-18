using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans;
using Orleans.Configuration;
using Orleans.Configuration.Overrides;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.AdoNet.Entity;
using Orleans.Transactions.AdoNet.Storage;
using Orleans.Transactions.AdoNet.Utils;

namespace Orleans.Transactions.AdoNet.TransactionalState;

/// <summary>
/// Creates ADO.NET transactional state storage instances.
/// </summary>
public class TransactionalStateStorageFactory : ITransactionalStateStorageFactory, ILifecycleParticipant<ISiloLifecycle>
{
    internal const int StateIdLength = SHA256.HashSizeInBytes * 2;

    private readonly string name;
    private readonly TransactionalStateStorageOptions options;
    private readonly ClusterOptions clusterOptions;
    private readonly JsonSerializerSettings jsonSettings;

    /// <summary>
    /// Initializes a new transactional state storage factory.
    /// </summary>
    public TransactionalStateStorageFactory(
        string name,
        TransactionalStateStorageOptions options,
        IOptions<ClusterOptions> clusterOptions,
        IServiceProvider services)
    {
        this.name = name;
        this.options = options;
        this.clusterOptions = clusterOptions.Value;
        this.jsonSettings = TransactionalStateFactory.GetJsonSerializerSettings(services);
    }

    /// <summary>
    /// Creates a transactional state storage factory from registered services.
    /// </summary>
    public static ITransactionalStateStorageFactory Create(IServiceProvider services, string name)
    {
        var optionsMonitor = services.GetRequiredService<IOptionsMonitor<TransactionalStateStorageOptions>>();
        return ActivatorUtilities.CreateInstance<TransactionalStateStorageFactory>(services, name, optionsMonitor.Get(name));
    }

    /// <inheritdoc />
    public ITransactionalStateStorage<TState> Create<TState>(
        string stateName,
        IGrainContext context) where TState : class, new()
    {
        string partitionKey = MakePartitionKey(context, stateName);
        return ActivatorUtilities.CreateInstance<TransactionalStateStorage<TState>>(context.ActivationServices, partitionKey, this.jsonSettings, this.options);
    }

    private string MakePartitionKey(IGrainContext context, string stateName)
    {
        string grainKey = context.GrainReference.GrainId.ToString();
        var key = CreateStateId(grainKey, clusterOptions.ServiceId, stateName);
        return ValidateStateId(key);
    }

    internal static string CreateStateId(string grainKey, string serviceId, string stateName)
    {
        var value = $"{grainKey.Length}:{grainKey}{serviceId.Length}:{serviceId}{stateName.Length}:{stateName}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private string ValidateStateId(string key)
    {
        if (key.Length > this.options.StateIdKeyMaxLength)
        {
            throw new ArgumentException($"Key length {key.Length} is too long. Key={key}", nameof(key));
        }

        return key;
    }

    /// <inheritdoc />
    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(OptionFormattingUtilities.Name<TransactionalStateStorageFactory>(name), this.options.InitStage, Init);
    }

    private Task Init(CancellationToken cancellationToken) => Task.CompletedTask;
}
