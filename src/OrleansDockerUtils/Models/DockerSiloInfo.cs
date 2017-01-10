using Newtonsoft.Json;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using System.Net;

namespace Microsoft.Orleans.Docker.Models
{
    /// <summary>
    /// Represents silo endpoints.
    /// </summary>
    public class DockerSiloInfo
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
        /// Gets the parsed silo address.
        /// </summary>
        [JsonIgnore]
        public SiloAddress SiloAddress => string.IsNullOrWhiteSpace(Silo) ? null : SiloAddress.FromParsableString(Silo);

        /// <summary>
        /// Gets the parsed gateway address.
        /// </summary>
        [JsonIgnore]
        public SiloAddress GatewayAddress => string.IsNullOrWhiteSpace(Gateway) ? null : SiloAddress.FromParsableString(Gateway);

        /// <summary>
        /// Initializes a new instance of the <see cref="DockerSiloInfo" /> class.
        /// </summary>
        /// <param name="siloName">The silo name.</param>
        /// <param name="siloEndPoint">The Inter-silo <see cref="IPEndPoint"/>.</param>
        /// <param name="gatewayEndPoint">The Gateway <see cref="IPEndPoint"/>.</param>
        /// <param name="generation">The silo Generation.</param>
        public DockerSiloInfo(string siloName, IPEndPoint siloEndPoint, IPEndPoint gatewayEndPoint, int generation)
        {
            Name = siloName;
            Silo = SiloAddress.New(siloEndPoint, generation).ToParsableString(); 
            Gateway = gatewayEndPoint != null ? SiloAddress.New(gatewayEndPoint, generation).ToParsableString() : null;
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return $"{nameof(Name)}: {Name}, {nameof(SiloAddress)}: {SiloAddress}, {nameof(GatewayAddress)}: {GatewayAddress}";
        }
    }
}
