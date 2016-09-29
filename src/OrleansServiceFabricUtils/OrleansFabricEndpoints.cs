using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Microsoft.Orleans.ServiceFabric
{
    /// <summary>
    /// Represents silo endpoints.
    /// </summary>
    public class OrleansFabricEndpoints
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansFabricEndpoints" /> class.
        /// </summary>
        /// <param name="config">
        /// The node configuration to initialize from.
        /// </param>
        public OrleansFabricEndpoints(NodeConfiguration config)
        {
            this.Silo = SiloAddress.New(config.Endpoint, config.Generation).ToParsableString();
            this.Gateway = SiloAddress.New(config.ProxyGatewayEndpoint, config.Generation).ToParsableString();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansFabricEndpoints" /> class.
        /// </summary>
        public OrleansFabricEndpoints() { }

        /// <summary>
        /// Gets or sets the silo address.
        /// </summary>
        [JsonProperty("silo")]
        public string Silo { get; set; }

        /// <summary>
        /// Gets or sets the gateway address.
        /// </summary>
        [JsonProperty("gateway")]
        public string Gateway { get; set; }

        /// <summary>
        /// Gets the parsed silo address.
        /// </summary>
        [JsonIgnore]
        public SiloAddress SiloAddress => SiloAddress.FromParsableString(this.Silo);

        /// <summary>
        /// Gets the parsed gateway address.
        /// </summary>
        [JsonIgnore]
        public SiloAddress GatewayAddress => SiloAddress.FromParsableString(this.Gateway);
    }
}
