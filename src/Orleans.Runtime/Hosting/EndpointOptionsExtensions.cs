using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Orleans.Hosting
{
    internal static class EndpointOptionsExtensions
    {
        private static IPAddress ResolveIPAddress(this EndpointOptions options)
        {
            if (options.IPAddress != null)
                return options.IPAddress;

            var family = options.NetworkAddress.AddressFamily;
            var loopback = family == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            var any = family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;

            // IF the address is an empty string, default to the local machine
            if (string.IsNullOrEmpty(options.HostNameOrIPAddress))
            {
                options.HostNameOrIPAddress = Dns.GetHostName();
            }

            // Fix StreamFilteringTests_SMS tests
            if (options.HostNameOrIPAddress.Equals("loopback", StringComparison.OrdinalIgnoreCase))
            {
                return loopback;
            }

            // check if addrOrHost is a valid IP address including loopback (127.0.0.0/8, ::1) and any (0.0.0.0/0, ::) addresses
            IPAddress address;
            if (IPAddress.TryParse(options.HostNameOrIPAddress, out address))
            {
                return address;
            }

            var candidates = new List<IPAddress>();

            // Get IP address from DNS. If addrOrHost is localhost will 
            // return loopback IPv4 address (or IPv4 and IPv6 addresses if OS is supported IPv6)
            var nodeIps = Dns.GetHostAddresses(options.HostNameOrIPAddress);
            foreach (var nodeIp in nodeIps.Where(x => x.AddressFamily == family))
            {
                // If the subnet does not match - we can't resolve this address.
                // If subnet is not specified - pick smallest address deterministically.
                if (options.NetworkAddress.Address == any)
                {
                    candidates.Add(nodeIp);
                }
                else
                {
                    var ip = nodeIp;
                    if (options.NetworkAddress.Contains(ip))
                    {
                        candidates.Add(nodeIp);
                    }
                }
            }
            if (candidates.Count > 0)
            {
                return candidates.First();
            }
            throw new ArgumentException("Hostname '" + options.HostNameOrIPAddress + "' with subnet " + options.NetworkAddress + " and family " + family + " is not a valid IP address or DNS name");
        }

        public static IPEndPoint ResolveEndpoint(this EndpointOptions options)
        {
            return new IPEndPoint(options.ResolveIPAddress(), options.Port);
        }

    }
}