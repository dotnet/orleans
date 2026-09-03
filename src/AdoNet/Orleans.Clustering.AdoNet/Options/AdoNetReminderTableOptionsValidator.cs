using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;

namespace Orleans.Configuration
{
    /// <summary>
    /// Validates <see cref="AdoNetClusteringSiloOptions"/> configuration.
    /// </summary>
    public class AdoNetClusteringSiloOptionsValidator : IConfigurationValidator
    {
        private readonly AdoNetClusteringSiloOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="AdoNetClusteringSiloOptionsValidator"/> class.
        /// </summary>
        /// <param name="options">The options to validate.</param>
        public AdoNetClusteringSiloOptionsValidator(IOptions<AdoNetClusteringSiloOptions> options)
        {
            this.options = options.Value;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(this.options.Invariant))
            {
                throw new OrleansConfigurationException($"Invalid {nameof(AdoNetClusteringSiloOptions)} values for {nameof(AdoNetClusteringTable)}. {nameof(options.Invariant)} is required.");
            }

            if (string.IsNullOrWhiteSpace(this.options.ConnectionString) == (this.options.DataSource is null))
            {
                throw new OrleansConfigurationException($"Invalid {nameof(AdoNetClusteringSiloOptions)} values for {nameof(AdoNetClusteringTable)}. Configure exactly one of {nameof(options.ConnectionString)} or {nameof(options.DataSource)}.");
            }
        }
    }
}
