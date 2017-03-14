namespace Microsoft.Orleans.ServiceFabric.Models
{
    using global::Orleans.Runtime;
    using global::Orleans.Runtime.Configuration;

    using Newtonsoft.Json;

    /// <summary>
    /// Represents silo endpoints.
    /// </summary>
    public class FabricSiloInfo
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FabricSiloInfo" /> class.
        /// </summary>
        /// <param name="config">
        /// The node configuration to initialize from.
        /// </param>
        public FabricSiloInfo(NodeConfiguration config)
        {
            this.Name = config.SiloName;
            this.Silo = SiloAddress.New(config.Endpoint, config.Generation).ToParsableString();
            this.Gateway = SiloAddress.New(config.ProxyGatewayEndpoint, config.Generation).ToParsableString();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FabricSiloInfo" /> class.
        /// </summary>
        public FabricSiloInfo() { }

        /// <summary>
        /// Gets the name of the silo.
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// Gets the silo address.
        /// </summary>
        [JsonProperty("silo")]
        public string Silo { get; set; }

        /// <summary>
        /// Gets the gateway address.
        /// </summary>
        [JsonProperty("gw")]
        public string Gateway { get; set; }

        /// <summary>
        /// Gets the parsed silo address.
        /// </summary>
        [JsonIgnore]
        public SiloAddress SiloAddress => string.IsNullOrWhiteSpace(this.Silo) ? null : SiloAddress.FromParsableString(this.Silo);

        /// <summary>
        /// Gets the parsed gateway address.
        /// </summary>
        [JsonIgnore]
        public SiloAddress GatewayAddress => string.IsNullOrWhiteSpace(this.Gateway) ? null : SiloAddress.FromParsableString(this.Gateway);

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{nameof(this.Name)}: {this.Name}, {nameof(this.SiloAddress)}: {this.SiloAddress}, {nameof(this.GatewayAddress)}: {this.GatewayAddress}";
        }
    }
}
