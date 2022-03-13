using System;
using Consul;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Base class for consul-cluster-options.
    /// </summary>
    public abstract class ConsulClusteringAbstractOptions
    {
        /// <summary>
        /// Consul KV root folder name.
        /// </summary>
        public string KvRootFolder { get; set; }

        /// <summary>
        /// Address for ConsulClient
        /// </summary>        
        public Uri Address { get; set; }

        /// <summary>
        /// ACL Client Token
        /// </summary>
        public string AclClientToken { get; set; }

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
        public void ConfigureConsulClient(Uri address)
        {
            ConfigureConsulClient(address, string.Empty);
        }

        /// <summary>
        /// Configures the <see cref="CreateClient"/> using the consul-address and a acl-token.
        /// </summary>
        public void ConfigureConsulClient(Uri address, string aclClientToken)
        {
            if (address is null) throw new ArgumentNullException(nameof(address));
            if (aclClientToken is null) throw new ArgumentNullException(nameof(aclClientToken));
            
            CreateClient = () => new ConsulClient(config =>
            {
                config.Address = address;
                config.Token = aclClientToken;
            });
        }

        public ConsulClusteringAbstractOptions()
        {
            this.CreateClient = () =>
               new ConsulClient(config =>
               {
                   config.Address = this.Address;
                   config.Token = this.AclClientToken;
               });
        }

        internal void Validate(string name)
        {
            if (CreateClient is null)
            {
                throw new OrleansConfigurationException($"No callback specified. Use the {GetType().Name}.{nameof(ConsulClusteringAbstractOptions.ConfigureConsulClient)} method to configure the consul client.");
            }                       
        }
    }

    public class ConsulClusteringAbstractOptionsValidator<TOptions> : IConfigurationValidator where TOptions : ConsulClusteringAbstractOptions
    {
        public ConsulClusteringAbstractOptionsValidator(TOptions options, string name = null)
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
