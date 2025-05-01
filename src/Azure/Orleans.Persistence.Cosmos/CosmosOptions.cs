// <copyright file="CosmosOptions.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

using Azure;
using Azure.Core;

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Options for Azure Cosmos DB storage.
/// </summary>
public abstract class CosmosOptions
{
    /// <summary>
    /// Gets or sets the name of the database to use for grain state.
    /// </summary>
    public string DatabaseName { get; set; } = "Orleans";

    /// <summary>
    /// Gets or sets the name of the container to use to store grain state.
    /// </summary>
    public string ContainerName { get; set; } = default!;

    /// <summary>
    /// Gets or sets the options passed to the Cosmos DB client, or <see langword="null"/> to use default options.
    /// </summary>
    public CosmosClientOptions? ClientOptions { get; set; }

    /// <summary>
    /// Gets factory method for creating a <see cref="CosmosClient"/>.
    /// </summary>
    public Func<IServiceProvider, ValueTask<CosmosClient>> CreateClient { get; private set; } = null!;

    /// <summary>
    /// Configures the Cosmos DB client.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    /// <see cref="CosmosClient(string, CosmosClientOptions)"/>
    public void ConfigureCosmosClient(string connectionString)
    {
        this.CreateClient = _ => new (new CosmosClient(connectionString, this.ClientOptions));
    }

    /*

    // NOTE: Cosmos DB 3.27.0 was being used when this code was introduced and it does not support this constructor, while later versions do.

    /// <summary>
    /// Configures the Cosmos DB client.
    /// </summary>
    /// <param name="accountEndpoint">The account endpoint. In the form of <c>https://{databaseaccount}.documents.azure.com:443/</c>, <see href="https://learn.microsoft.com/rest/api/cosmos-db/cosmosdb-resource-uri-syntax-for-rest"/>.</param>
    /// <param name="authKeyOrResourceTokenCredential"><see cref="AzureKeyCredential"/> with master-key or resource token.</param>
    public void ConfigureCosmosClient(string accountEndpoint, AzureKeyCredential authKeyOrResourceTokenCredential)
    {
        this.CreateClient = _ => new (new CosmosClient(accountEndpoint, authKeyOrResourceTokenCredential, this.ClientOptions));
    }
    */

    /// <summary>
    /// Configures the Cosmos DB client.
    /// </summary>
    /// <param name="accountEndpoint">The account endpoint. In the form of <c>https://{databaseaccount}.documents.azure.com:443/</c>, <see href="https://learn.microsoft.com/rest/api/cosmos-db/cosmosdb-resource-uri-syntax-for-rest"/>.</param>
    /// <param name="tokenCredential">The token to provide AAD for authorization.</param>
    /// <see cref="CosmosClient(string, TokenCredential, CosmosClientOptions)"/>
    public void ConfigureCosmosClient(string accountEndpoint, TokenCredential tokenCredential)
    {
        this.CreateClient = _ => new (new CosmosClient(accountEndpoint, tokenCredential, this.ClientOptions));
    }

    /// <summary>
    /// Configures the Cosmos DB client.
    /// </summary>
    /// <param name="accountEndpoint">The account endpoint. In the form of <c>https://{databaseaccount}.documents.azure.com:443/</c>, <see href="https://learn.microsoft.com/rest/api/cosmos-db/cosmosdb-resource-uri-syntax-for-rest"/>.</param>
    /// <param name="authKeyOrResourceToken">The Cosmos account key or resource token to use to create the client.</param>
    /// <see cref="CosmosClient(string, TokenCredential, CosmosClientOptions)"/>
    public void ConfigureCosmosClient(string accountEndpoint, string authKeyOrResourceToken)
    {
        this.CreateClient = _ => new (new CosmosClient(accountEndpoint, authKeyOrResourceToken, this.ClientOptions));
    }

    /// <summary>
    /// Configures the Cosmos DB client.
    /// </summary>
    /// <param name="createClient">The delegate used to create the Cosmos DB client.</param>
    public void ConfigureCosmosClient(Func<IServiceProvider, ValueTask<CosmosClient>> createClient)
    {
        this.CreateClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
    }
}
