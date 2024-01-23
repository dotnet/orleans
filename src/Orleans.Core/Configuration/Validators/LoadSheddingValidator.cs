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

    /// <inheritdoc />
    public void ValidateConfiguration()
    {
        // When Load Shedding is disabled, don't validate configuration.
        if (!_loadSheddingOptions.LoadSheddingEnabled)
        {
            return;
        }

        if (_loadSheddingOptions.CpuThreshold > 100 || _loadSheddingOptions.CpuThreshold <= 0)
        {
            throw new OrleansConfigurationException($"Limit '{nameof(LoadSheddingOptions)}.{nameof(LoadSheddingOptions.CpuThreshold)}' must be greater than 0% and less than or equal to 100%.");
        }

        if (_loadSheddingOptions.MemoryThreshold > 100 || _loadSheddingOptions.MemoryThreshold <= 0)
        {
            throw new OrleansConfigurationException($"Limit '{nameof(LoadSheddingOptions)}.{nameof(LoadSheddingOptions.MemoryThreshold)}' must be greater than 0% and less than or equal to 100%.");
        }
    }
}
