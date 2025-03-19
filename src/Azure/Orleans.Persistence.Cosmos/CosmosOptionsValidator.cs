// <copyright file="CosmosOptionsValidator.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Validates instances of <see cref="CosmosOptions"/>.
/// </summary>
/// <typeparam name="TOptions">The options type.</typeparam>
public class CosmosOptionsValidator<TOptions> : IConfigurationValidator
    where TOptions : CosmosOptions
{
    private readonly TOptions options;
    private readonly string name;

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosOptionsValidator{TOptions}"/> class.
    /// </summary>
    /// <param name="options">The instance to be validated.</param>
    /// <param name="name">The option name to be validated.</param>
    public CosmosOptionsValidator(TOptions options, string name)
    {
        this.options = options;
        this.name = name;
    }

    /// <inheritdoc/>
    public void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(this.options.DatabaseName))
        {
            throw new OrleansConfigurationException(
                $"Configuration for Azure Cosmos DB provider '{this.name}' is invalid. '{nameof(this.options.DatabaseName)}' is not valid.");
        }

        if (string.IsNullOrWhiteSpace(this.options.ContainerName))
        {
            throw new OrleansConfigurationException(
                $"Configuration for Azure Cosmos DB provider '{this.name}' is invalid. '{nameof(this.options.ContainerName)}' is not valid.");
        }

        if (this.options.CreateClient is null)
        {
            throw new OrleansConfigurationException(
                $"Configuration for Azure Cosmos DB provider '{this.name}' is invalid. You must call '{nameof(this.options.ConfigureCosmosClient)}' to configure access to Azure Cosmos DB.");
        }
    }
}