using System;
using Consul;

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
        internal Func<IConsulClient> CreateClientCallback { get; private set; }       

        /// <summary>
        /// Configures the <see cref="CreateClientCallback"/> using the provided callback.
        /// </summary>
        public void ConfigureConsulClient(Func<IConsulClient> createClientCallback)
        {
            CreateClientCallback = createClientCallback ?? throw new ArgumentNullException(nameof(createClientCallback));
        }

        /// <summary>
        /// Configures the <see cref="CreateClientCallback"/> using the consul-address and a acl-token.
        /// </summary>
        public void ConfigureConsulClient(Uri address)
        {
           ConfigureConsulClient(address, string.Empty);
        }

        /// <summary>
        /// Configures the <see cref="CreateClientCallback"/> using the consul-address and a acl-token.
        /// </summary>
        public void ConfigureConsulClient(Uri address, string aclClientToken)
        {
            if (address is null) throw new ArgumentNullException(nameof(address));
            if (aclClientToken is null) throw new ArgumentNullException(nameof(aclClientToken));

            CreateClientCallback = () => new ConsulClient(config =>
            {
                config.Address = address;
                config.Token = aclClientToken;
            });
        }

        /// <summary>
        /// Creates a consul client, if a callback was set this will be used. For compatibility reasons this.
        /// </summary>
        /// <returns></returns>
        internal IConsulClient CreateClient()
        {
            return this.CreateClientCallback?.Invoke() ??
                new ConsulClient(config =>
                {
                    config.Address = this.Address;
                    config.Token = this.AclClientToken;
                });
        }
    }
}
