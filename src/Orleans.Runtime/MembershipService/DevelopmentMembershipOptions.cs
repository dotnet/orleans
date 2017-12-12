using System.Net;

namespace Orleans.Hosting
{
    /// <summary>Configures development clustering options</summary>
    public class DevelopmentMembershipOptions
    {
        /// <summary>
        /// Gets or sets the seed node to find the membership system grain.
        /// </summary>
        public IPEndPoint PrimarySiloEndpoint { get; set; }
    }
}