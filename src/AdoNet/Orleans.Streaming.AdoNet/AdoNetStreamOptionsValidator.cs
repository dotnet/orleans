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
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming. {nameof(_options.ConnectionString)} is required.");
        }

        if (_options.MaxAttempts <= 0)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming. {nameof(_options.MaxAttempts)} must be greater than 0.");
        }

        if (_options.SweepPeriod < TimeSpan.FromSeconds(1))
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming. {nameof(_options.SweepPeriod)} must be at least 1 second.");
        }

        if (_options.ExpiryTimeout <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming. {nameof(_options.ExpiryTimeout)} must be greater than zero.");
        }

        if (_options.RemovalTimeout <= TimeSpan.Zero)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetStreamOptions)} values for ADO.NET Streaming. {nameof(_options.RemovalTimeout)} must be greater than zero.");
        }
    }
}