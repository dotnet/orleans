using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;

namespace Orleans.Configuration
{
    /// <summary>
    /// Validates <see cref="AdoNetClusteringClientOptions"/> configuration.
    /// </summary>
    public class AdoNetClusteringClientOptionsValidator : IConfigurationValidator
    {
        private readonly AdoNetClusteringClientOptions options;

        public AdoNetClusteringClientOptionsValidator(IOptions<AdoNetClusteringClientOptions> options)
        {
            this.options = options.Value;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(this.options.Invariant))
            {
                throw new OrleansConfigurationException($"Invalid {nameof(AdoNetClusteringClientOptions)} values for {nameof(AdoNetClusteringTable)}. {nameof(options.Invariant)} is required.");
            }

            if (string.IsNullOrWhiteSpace(this.options.ConnectionString))
            {
                throw new OrleansConfigurationException($"Invalid {nameof(AdoNetClusteringClientOptions)} values for {nameof(AdoNetClusteringTable)}. {nameof(options.ConnectionString)} is required.");
            }
        }
    }
}