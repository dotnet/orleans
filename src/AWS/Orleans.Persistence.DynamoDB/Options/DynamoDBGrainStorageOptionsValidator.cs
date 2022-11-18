using Orleans.Runtime;

namespace Orleans.Configuration;

/// <summary>
/// Configuration validator for DynamoDBStorageOptions
/// </summary>
public class DynamoDBGrainStorageOptionsValidator : IConfigurationValidator
{
    private readonly DynamoDBStorageOptions options;
    private readonly string name;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="options">The option to be validated.</param>
    /// <param name="name">The option name to be validated.</param>
    public DynamoDBGrainStorageOptionsValidator(DynamoDBStorageOptions options, string name)
    {
        this.options = options;
        this.name = name;
    }

    public void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(this.options.TableName))
            throw new OrleansConfigurationException(
                $"Configuration for DynamoDBGrainStorage {this.name} is invalid. {nameof(this.options.TableName)} is not valid.");

        if (this.options.UseProvisionedThroughput)
        {
            if (this.options.ReadCapacityUnits == 0)
                throw new OrleansConfigurationException(
                    $"Configuration for DynamoDBGrainStorage {this.name} is invalid. {nameof(this.options.ReadCapacityUnits)} is not valid.");

            if (this.options.WriteCapacityUnits == 0)
                throw new OrleansConfigurationException(
                    $"Configuration for DynamoDBGrainStorage {this.name} is invalid. {nameof(this.options.WriteCapacityUnits)} is not valid.");
        }
    }
}