using static System.String;

namespace Orleans.Configuration;

/// <summary>
/// Validates <see cref="AdoNetGrainDirectoryOptions"/> configuration.
/// </summary>
/// <param name="options">The options to validate.</param>
/// <param name="name">The name of the grain directory.</param>
public class AdoNetGrainDirectoryOptionsValidator(AdoNetGrainDirectoryOptions options, string name) : IConfigurationValidator
{
    /// <inheritdoc />
    public void ValidateConfiguration()
    {
        if (options is null)
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetGrainDirectoryOptions)} values for {nameof(AdoNetGrainDirectory)}|{name}. {nameof(options)} is required.");
        }

        if (IsNullOrWhiteSpace(options.Invariant))
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetGrainDirectoryOptions)} values for {nameof(AdoNetGrainDirectory)}|{name}. {nameof(options.Invariant)} is required.");
        }

        if (IsNullOrWhiteSpace(options.ConnectionString) == (options.DataSource is null))
        {
            throw new OrleansConfigurationException($"Invalid {nameof(AdoNetGrainDirectoryOptions)} values for {nameof(AdoNetGrainDirectory)}|{name}. Configure exactly one of {nameof(options.ConnectionString)} or {nameof(options.DataSource)}.");
        }
    }
}
