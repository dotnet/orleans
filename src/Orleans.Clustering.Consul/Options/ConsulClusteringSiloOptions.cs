using System;

namespace Orleans.Configuration
{
    /// <summary>
    /// Options for configuring ConsulBasedMembership
    /// </summary>
    public class ConsulClusteringSiloOptions
    {
        /// <summary>
        /// Address for consul client
        /// </summary>
        public Uri Address { get; set; }
        
        /// <summary>
        /// ACL Client Token
        /// </summary>
        public string AclClientToken { get; set; }
        /// <summary>
        /// Consul KV root folder name.
        /// </summary>
       public string KvRootFolder { get; set; }
    }
}
