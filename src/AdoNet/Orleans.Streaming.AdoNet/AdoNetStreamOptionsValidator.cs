using static System.String;

namespace Orleans.Configuration;

/// <summary>
/// Validates <see cref="AdoNetStreamOptions"/> configuration.
/// </summary>
public class AdoNetStreamOptionsValidator(AdoNetStreamOptions options, string name) : IConfigurationValidator
{
    /// <inheritdoc />
    public void ValidateConfiguration()
    {
        if (IsNullOrWhiteSpace(options.Invariant))
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.Invariant)} is required.");
        }

        if (IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.ConnectionString)} is required.");
        }

        if (options.MaxAttempts < 0)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.MaxAttempts)} must be greater than zero.");
        }

        if (options.VisibilityTimeout < TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.VisibilityTimeout)} must be greater than zero.");
        }

        if (options.EvictionInterval < TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.EvictionInterval)} must be greater than zero.");
        }

        if (options.ExpiryTimeout < TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.ExpiryTimeout)} must be greater than zero.");
        }

        if (options.DeadLetterEvictionTimeout < TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.DeadLetterEvictionTimeout)} must be greater than zero.");
        }

        if (options.EvictionBatchSize < 0)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.EvictionBatchSize)} must be greater than zero.");
        }
    }
}