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

        if (IsNullOrWhiteSpace(options.ConnectionString) == (options.DataSource is null))
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': configure exactly one of {nameof(options.ConnectionString)} or {nameof(options.DataSource)}.");
        }

        if (options.MaxMessagesPerRead <= 0)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.MaxMessagesPerRead)} must be greater than zero.");
        }

        if (options.CheckpointPersistInterval <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.CheckpointPersistInterval)} must be greater than zero.");
        }

        if (options.RetentionPeriod <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.RetentionPeriod)} must be greater than zero.");
        }

        if (options.MaximumRetentionPeriod is { } maximumRetentionPeriod && maximumRetentionPeriod < options.RetentionPeriod)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.MaximumRetentionPeriod)} must be greater than or equal to {nameof(options.RetentionPeriod)}.");
        }

        if (options.CleanupInterval <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.CleanupInterval)} must be greater than zero.");
        }

        if (options.CleanupBatchSize <= 0)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.CleanupBatchSize)} must be greater than zero.");
        }

        if (options.InitializationTimeout <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming Provider '{name}': {nameof(options.InitializationTimeout)} must be greater than zero.");
        }
    }
}