using System.Collections.Generic;

namespace Orleans.ServiceFabric
{
    using global::Orleans.Runtime;

    using Newtonsoft.Json;

    /// <summary>
    /// Represents silo endpoints.
    /// </summary>
    public class FabricSiloInfo
    {
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
        /// Gets the collection of other endpoints associated with this silo.
        /// </summary>
        [JsonProperty("other")]
        public Dictionary<string, string> OtherEndpoints { get; } = new Dictionary<string, string>();

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
