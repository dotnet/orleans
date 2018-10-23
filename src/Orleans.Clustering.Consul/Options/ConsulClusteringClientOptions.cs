using System;

namespace Orleans.Configuration
{
    public class ConsulClusteringClientOptions
    {
        /// <summary>
        /// Address for ConsulClient
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
