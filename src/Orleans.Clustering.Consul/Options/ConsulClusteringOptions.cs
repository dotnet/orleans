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
        public string KvRootFolder { get; set; }       

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
        public void ConfigureConsulClient(Uri address, string aclClientToken = null)
        {
            if (address is null) throw new ArgumentNullException(nameof(address));            
            
            CreateClient = () => new ConsulClient(config =>
            {
                config.Address = address;
                config.Token = aclClientToken;
            });
        }

        public ConsulClusteringOptions()
        {
            this.CreateClient = () => new ConsulClient();
        }

        internal void Validate(string name)
        {
            if (CreateClient is null)
            {
                throw new OrleansConfigurationException($"No callback specified. Use the {GetType().Name}.{nameof(ConsulClusteringOptions.ConfigureConsulClient)} method to configure the consul client.");
            }                       
        }
    }

    public class ConsulClusteringOptionsValidator<TOptions> : IConfigurationValidator where TOptions : ConsulClusteringOptions
    {
        public ConsulClusteringOptionsValidator(TOptions options, string name = null)
        {
            Options = options;
            Name = name;
        }

        public TOptions Options { get; }
        public string Name { get; }

        public virtual void ValidateConfiguration()
        {
            Options.Validate(Name);
        }
    }
}
