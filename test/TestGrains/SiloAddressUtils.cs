using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace UnitTests.Grains
{
    public static class SiloAddressUtils
    {
        private static readonly IPEndPoint localEndpoint = new IPEndPoint(GetLocalIPAddress(), 0); // non loopback local ip.

        /// <summary>
        /// Gets the address of the local server.
        /// If there are multiple addresses in the correct family in the server's DNS record, the first will be returned.
        /// </summary>
        /// <returns>The server's IPv4 address.</returns>
        private static IPAddress GetLocalIPAddress()
        {
            var family = AddressFamily.InterNetwork;
            var loopback = IPAddress.Loopback;
            // get list of all network interfaces
            NetworkInterface[] netInterfaces = NetworkInterface.GetAllNetworkInterfaces();

            var candidates = new List<IPAddress>();
            // loop through interfaces
            for (int i = 0; i < netInterfaces.Length; i++)
            {
                NetworkInterface netInterface = netInterfaces[i];

                if (netInterface.OperationalStatus != OperationalStatus.Up)
                {
                    // Skip network interfaces that are not operational
                    continue;
                }

                bool isLoopbackInterface = (netInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback);
                // get list of all unicast IPs from current interface
                UnicastIPAddressInformationCollection ipAddresses = netInterface.GetIPProperties().UnicastAddresses;

                // loop through IP address collection
                foreach (UnicastIPAddressInformation ip in ipAddresses)
                {
                    if (ip.Address.AddressFamily == family) // Picking the first address of the requested family for now. Will need to revisit later
                    {
                        //don't pick loopback address, unless we were asked for a loopback interface
                        if (!(isLoopbackInterface && ip.Address.Equals(loopback)))
                        {
                            candidates.Add(ip.Address); // collect all candidates.
                        }
                    }
                }
            }
            if (candidates.Count > 0) return candidates.Min();
            throw new Exception("Failed to get a local IP address.");
        }

        /// <summary>
        /// Factory for creating new SiloAddresses for silo on this machine with specified generation number.
        /// </summary>
        /// <param name="gen">Generation number of the silo.</param>
        /// <returns>SiloAddress object initialized with the non-loopback local IP address and the specified silo generation.</returns>
        public static SiloAddress NewLocalSiloAddress(int gen)
        {
            return SiloAddress.New(localEndpoint, gen);
        }
    }
}
