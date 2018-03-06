using System.Net;

namespace Orleans.Configuration
{
    /// <summary>Configures development clustering options</summary>
    public class DevelopmentClusteringOptions
    {
        /// <summary>
        /// Gets or sets the seed node to find the membership system grain.
        /// </summary>
        public IPEndPoint PrimarySiloEndpoint { get; set; }
    }
}