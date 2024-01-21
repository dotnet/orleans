using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration.Validators;

/// <summary>
/// Validates <see cref="LoadSheddingOptions"/> configuration.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="LoadSheddingValidator"/> class.
/// </remarks>
/// <param name="loadSheddingOptions">
/// The load shedding options.
/// </param>
internal class LoadSheddingValidator(IOptions<LoadSheddingOptions> loadSheddingOptions) : IConfigurationValidator
{
    private readonly LoadSheddingOptions _loadSheddingOptions = loadSheddingOptions.Value;

    internal const string InvalidLimit = "Limit cannot exceed 100%.";

    /// <inheritdoc />
    public void ValidateConfiguration()
    {
        // When Load Shedding is disabled, don't validate configuration.
        if (!_loadSheddingOptions.LoadSheddingEnabled)
        {
            return;
        }

        if (_loadSheddingOptions.LoadSheddingLimit > 100)
        {
            throw new OrleansConfigurationException(InvalidLimit);
        }

        if (_loadSheddingOptions.MemoryLoadLimit > 100)
        {
            throw new OrleansConfigurationException(InvalidLimit);
        }
    }
}
