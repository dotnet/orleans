using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures the Orleans cluster.
    /// </summary>
    public class ClusterOptions
    {
        /// <summary>
        /// The default value of <see cref="ClusterId"/>.
        /// </summary>
        public const string DefaultClusterId = "default";

        /// <summary>
        /// The default value of <see cref="ServiceId"/>.
        /// </summary>
        public const string DefaultServiceId = "default";

        /// <summary>
        /// Default cluster id for development clusters.
        /// </summary>
        internal const string DevelopmentClusterId = "dev";

        /// <summary>
        /// Default service id for development clusters.
        /// </summary>
        internal const string DevelopmentServiceId = "dev";

        /// <summary>
        /// Gets or sets the cluster identity. This used to be called DeploymentId before Orleans 2.0 name.
        /// </summary>
        public string ClusterId { get; set; } = DefaultClusterId;

        /// <summary>
        /// Gets or sets a unique identifier for this service, which should survive deployment and redeployment, where as <see cref="ClusterId"/> might not.
        /// </summary>
        public string ServiceId { get; set; } = DefaultServiceId;
    }

    /// <summary>
    /// Validator for <see cref="ClusterOptions"/>
    /// </summary>
    public class ClusterOptionsValidator : IConfigurationValidator
    {
        private readonly ClusterOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterOptionsValidator"/> class.
        /// </summary>
        /// <param name="options">
        /// The options.
        /// </param>
        public ClusterOptionsValidator(IOptions<ClusterOptions> options)
        {
            this.options = options.Value;
        }

        /// <inheritdoc />
        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(this.options.ClusterId))
            {
                throw new OrleansConfigurationException(
                    $"Configuration for {nameof(ClusterOptions)} is invalid. " +
                    $"A non-empty value for {nameof(options.ClusterId)} is required. " +
                    $"See {Constants.TroubleshootingHelpLink} for more information.");
            }

            if (string.IsNullOrWhiteSpace(this.options.ServiceId))
            {
                throw new OrleansConfigurationException(
                    $"Configuration for {nameof(ClusterOptions)} is invalid. " +
                    $"A non-empty value for {nameof(options.ServiceId)} is required. " +
                    $"See {Constants.TroubleshootingHelpLink} for more information.");
            }
        }
    }
}
