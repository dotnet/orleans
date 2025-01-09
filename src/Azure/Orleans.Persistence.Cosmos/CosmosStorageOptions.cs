// <copyright file="CosmosStorageOptions.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

using Orleans.Core;

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Options for Azure Cosmos DB grain persistence.
/// </summary>
public class CosmosGrainStorageOptions : CosmosOptions
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosGrainStorageOptions"/> class.
    /// </summary>
    public CosmosGrainStorageOptions()
    {
        this.ContainerName = "OrleansStorage";
    }

    /// <summary>
    /// Gets or sets stage of silo lifecycle where storage should be initialized. Storage must be initialized prior to use.
    /// </summary>
    public int InitStage { get; set; } = ServiceLifecycleStage.ApplicationServices;

    /// <summary>
    /// Gets or sets a value indicating whether state should be deleted when <see cref="IStorage.ClearStateAsync"/> is called.
    /// The default is <c>true</c>.
    /// </summary>
    public bool DeleteStateOnClear { get; set; } = true;
}
