using Microsoft.Extensions.Options;

namespace Orleans
{
    /// <summary>
    /// Describes a configuration validator which is called during client and silo initialization.
    /// </summary>
    public interface IConfigurationValidator
    {
        /// <summary>
        /// Validates system configuration and throws an exception if configuration is not valid.
        /// </summary>
        void ValidateConfiguration();
    }
}