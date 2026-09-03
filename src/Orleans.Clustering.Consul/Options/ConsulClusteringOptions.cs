using System;
using Consul;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Base class for consul-cluster-options.
    /// </summary>
    public class ConsulClusteringOptions
    {
        /// <summary>
        /// Consul KV root folder name.
        /// </summary>
        public string? KvRootFolder { get; set; }

        /// <summary>
        /// Factory for the used Consul-Client.
        /// </summary>
        public Func<IConsulClient> CreateClient { get; private set; }

        /// <summary>
        /// Configures the <see cref="CreateClient"/> using the provided callback.
        /// </summary>
        public void ConfigureConsulClient(Func<IConsulClient> createClientCallback)
        {
            CreateClient = createClientCallback ?? throw new ArgumentNullException(nameof(createClientCallback));
        }

        /// <summary>
        /// Configures the <see cref="CreateClient"/> using the consul-address and a acl-token.
        /// </summary>
        public void ConfigureConsulClient(Uri address, string? aclClientToken = null)
        {
            if (address is null) throw new ArgumentNullException(nameof(address));

            CreateClient = () => new ConsulClient(config =>
            {
                config.Address = address;
                config.Token = aclClientToken;
            });
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConsulClusteringOptions"/> class using the default Consul client configuration.
        /// </summary>
        public ConsulClusteringOptions()
        {
            this.CreateClient = () => new ConsulClient();
        }

        internal void Validate(string? name)
        {
            if (CreateClient is null)
            {
                throw new OrleansConfigurationException($"No callback specified. Use the {GetType().Name}.{nameof(ConsulClusteringOptions.ConfigureConsulClient)} method to configure the consul client.");
            }
        }
    }

    /// <summary>
    /// Validates Consul clustering options.
    /// </summary>
    /// <typeparam name="TOptions">The type of options to validate.</typeparam>
    public class ConsulClusteringOptionsValidator<TOptions> : IConfigurationValidator where TOptions : ConsulClusteringOptions
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ConsulClusteringOptionsValidator{TOptions}"/> class.
        /// </summary>
        /// <param name="options">The options to validate.</param>
        /// <param name="name">The configured options name.</param>
        public ConsulClusteringOptionsValidator(TOptions options, string? name = null)
        {
            Options = options;
            Name = name;
        }

        /// <summary>
        /// Gets the options to validate.
        /// </summary>
        public TOptions Options { get; }

        /// <summary>
        /// Gets the configured options name.
        /// </summary>
        public string? Name { get; }

        /// <inheritdoc />
        public virtual void ValidateConfiguration()
        {
            Options.Validate(Name);
        }
    }
}
