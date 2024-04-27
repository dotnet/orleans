using static System.String;

namespace Orleans.Configuration;

/// <summary>
/// Validates <see cref="AdoNetStreamOptions"/> configuration.
/// </summary>
public class AdoNetStreamOptionsValidator(IOptions<AdoNetStreamOptions> options) : IConfigurationValidator
{
    private readonly AdoNetStreamOptions _options = options.Value;

    /// <inheritdoc />
    public void ValidateConfiguration()
    {
        if (IsNullOrWhiteSpace(_options.Invariant))
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming. {nameof(_options.Invariant)} is required.");
        }

        if (IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO>NET Streaming. {nameof(_options.ConnectionString)} is required.");
        }

        if (_options.MaxAttempts <= 0)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming. {nameof(_options.MaxAttempts)} must be greater than 0.");
        }

        if (_options.MaxBatchSize <= 0)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming. {nameof(_options.MaxBatchSize)} must be greater than 0.");
        }
    }
}