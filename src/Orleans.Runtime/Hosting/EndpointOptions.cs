using Orleans.Runtime;
using System;
using System.Net;
using System.Net.Sockets;

namespace Orleans.Hosting
{
    /// <summary>
    /// Configures the Silo endpoint options
    /// </summary>
    public class EndpointOptions
    {
        /// <summary>
        /// The external IP address or host name used for clustering. IP address will be inferred from <see cref="NetworkAddress"/> if this is not
        /// directly specified.
        /// </summary>
        public string HostNameOrIPAddress { get; set; }

        /// <summary>
        /// The IP address used for clustering. Will be inferred from <see cref="HostNameOrIPAddress"/> or <see cref="NetworkAddress"/> if this is not directly specified.
        /// </summary>
        public IPAddress IPAddress { get; set; }

        /// <summary>
        /// Specify the subnet to determine to IP address used for clustering.
        /// If <see cref="IPAddress"/> is specified, this property will be ignored.
        /// </summary>
        public NetworkAddress NetworkAddress { get; set; } = new NetworkAddress();

        /// <summary>
        /// The port this silo uses for silo-to-silo communication.
        /// </summary>
        public int Port { get; set; }

        /// <summary>
        /// The port this silo uses for silo-to-client (gateway) communication. Specify 0 to disable gateway functionality.
        /// </summary>
        public int ProxyPort { get; set; }

        //public bool BindToAny { get; set; }
    }

    /// <summary>
    /// Represent a network address with its mask
    /// </summary>
    public class NetworkAddress
    {
        /// <summary>
        /// Address of the network
        /// </summary>
        public IPAddress Address { get; set; }

        /// <summary>
        /// Mask of the network
        /// </summary>
        public IPAddress Mask { get; set; }

        public AddressFamily AddressFamily => Address.AddressFamily;

        /// <summary>
        /// Create a new NetworkAddress
        /// </summary>
        public NetworkAddress()
            : this(IPAddress.Any, IPAddress.Any)
        { }

        /// <summary>
        /// Create a new NetworkAddress
        /// </summary>
        public NetworkAddress(IPAddress address, IPAddress mask)
        {
            if (address.AddressFamily != mask.AddressFamily)
                throw new ArgumentException("address and mask should have the same AddressFamily");
            this.Address = address;
            this.Mask = mask;
        }

        /// <summary>
        /// Return true if <paramref name="otherAddress"/> belongs to this network
        /// </summary>
        internal bool Contains(IPAddress otherAddress)
        {
            if (this.Address.AddressFamily != otherAddress.AddressFamily)
                return false;

            var otherAddressBytes = otherAddress.GetAddressBytes();
            var maskBytes = this.Mask.GetAddressBytes();

            for (var i = 0; i < otherAddressBytes.Length; i++)
            {
                otherAddressBytes[i] = (byte)(otherAddressBytes[i] & maskBytes[i]);
            }

            return new IPAddress(otherAddressBytes).Equals(this.Address);
        }

        public override string ToString()
        {
            return $"{this.Address} netmask {this.Mask}";
        }

    }
}