using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Utilities class for working with configuration.
    /// </summary>
    public static class ConfigUtilities
    {
        // Time spans are entered as a string of decimal digits, optionally followed by a unit string: "ms", "s", "m", "hr"
        internal static TimeSpan ParseTimeSpan(string input, string errorMessage)
        {
            long unitSize;
            string numberInput;
            var trimmedInput = input.Trim().ToLowerInvariant();
            if (trimmedInput.EndsWith("ms", StringComparison.Ordinal))
            {
                unitSize = 10000;
                numberInput = trimmedInput.Remove(trimmedInput.Length - 2).Trim();
            }
            else if (trimmedInput.EndsWith('s'))
            {
                unitSize = 1000 * 10000;
                numberInput = trimmedInput.Remove(trimmedInput.Length - 1).Trim();
            }
            else if (trimmedInput.EndsWith('m'))
            {
                unitSize = 60 * 1000 * 10000;
                numberInput = trimmedInput.Remove(trimmedInput.Length - 1).Trim();
            }
            else if (trimmedInput.EndsWith("hr", StringComparison.Ordinal))
            {
                unitSize = 60 * 60 * 1000 * 10000L;
                numberInput = trimmedInput.Remove(trimmedInput.Length - 2).Trim();
            }
            else
            {
                unitSize = 1000 * 10000; // Default is seconds
                numberInput = trimmedInput;
            }
            decimal rawTimeSpan;
            if (!decimal.TryParse(numberInput, NumberStyles.Any, CultureInfo.InvariantCulture, out rawTimeSpan))
            {
                throw new FormatException(errorMessage + ". Tried to parse " + input);
            }
            return TimeSpan.FromTicks((long)(rawTimeSpan * unitSize));
        }

        internal static IPAddress ResolveIPAddressOrDefault(byte[] subnet, AddressFamily family)
        {
            IList<IPAddress> nodeIps = NetworkInterface.GetAllNetworkInterfaces()
                            .Where(iface => iface.OperationalStatus == OperationalStatus.Up)
                            .SelectMany(iface => iface.GetIPProperties().UnicastAddresses)
                            .Select(addr => addr.Address)
                            .Where(addr => addr.AddressFamily == family && !IPAddress.IsLoopback(addr))
                            .ToList();

            var ipAddress = PickIPAddress(nodeIps, subnet, family);
            return ipAddress;
        }

        internal static IPAddress ResolveIPAddressOrDefault(string addrOrHost, byte[] subnet, AddressFamily family)
        {
            var loopback = family == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;

            // if the address is an empty string, just enumerate all ip addresses available
            // on this node
            if (string.IsNullOrEmpty(addrOrHost))
            {
                return ResolveIPAddressOrDefault(subnet, family);
            }
            else
            {
                // Fix StreamFilteringTests_SMS tests
                if (addrOrHost.Equals("loopback", StringComparison.OrdinalIgnoreCase))
                {
                    return loopback;
                }

                // check if addrOrHost is a valid IP address including loopback (127.0.0.0/8, ::1) and any (0.0.0.0/0, ::) addresses
                if (IPAddress.TryParse(addrOrHost, out var address))
                {
                    return address;
                }

                // Get IP address from DNS. If addrOrHost is localhost will 
                // return loopback IPv4 address (or IPv4 and IPv6 addresses if OS is supported IPv6)
                var nodeIps = Dns.GetHostAddresses(addrOrHost);
                return PickIPAddress(nodeIps, subnet, family);
            }
        }

        private static IPAddress PickIPAddress(IList<IPAddress> nodeIps, byte[] subnet, AddressFamily family)
        {
            var candidates = new List<IPAddress>();
            foreach (var nodeIp in nodeIps.Where(x => x.AddressFamily == family))
            {
                // If the subnet does not match - we can't resolve this address.
                // If subnet is not specified - pick smallest address deterministically.
                if (subnet == null)
                {
                    candidates.Add(nodeIp);
                }
                else
                {
                    var ip = nodeIp;
                    if (subnet.Select((b, i) => ip.GetAddressBytes()[i] == b).All(x => x))
                    {
                        candidates.Add(nodeIp);
                    }
                }
            }

            return candidates.Count > 0 ? PickIPAddress(candidates) : null;
        }

        private static IPAddress PickIPAddress(IReadOnlyList<IPAddress> candidates)
        {
            IPAddress chosen = null;
            foreach (IPAddress addr in candidates)
            {
                if (chosen == null)
                {
                    chosen = addr;
                }
                else
                {
                    if (CompareIPAddresses(addr, chosen)) // pick smallest address deterministically
                        chosen = addr;
                }
            }
            return chosen;

            // returns true if lhs is "less" (in some repeatable sense) than rhs
            static bool CompareIPAddresses(IPAddress lhs, IPAddress rhs)
            {
                byte[] lbytes = lhs.GetAddressBytes();
                byte[] rbytes = rhs.GetAddressBytes();

                if (lbytes.Length != rbytes.Length) return lbytes.Length < rbytes.Length;

                // compare starting from most significant octet.
                // 10.68.20.21 < 10.98.05.04
                for (int i = 0; i < lbytes.Length; i++)
                {
                    if (lbytes[i] != rbytes[i])
                    {
                        return lbytes[i] < rbytes[i];
                    }
                }
                // They're equal
                return false;
            }
        }

        /// <summary>
        /// Gets the address of the local server.
        /// If there are multiple addresses in the correct family in the server's DNS record, the first will be returned.
        /// </summary>
        /// <returns>The server's IPv4 address.</returns>
        internal static IPAddress GetLocalIPAddress(AddressFamily family = AddressFamily.InterNetwork, string interfaceName = null)
        {
            var loopback = (family == AddressFamily.InterNetwork) ? IPAddress.Loopback : IPAddress.IPv6Loopback;
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
                if (!string.IsNullOrWhiteSpace(interfaceName) &&
                    !netInterface.Name.StartsWith(interfaceName, StringComparison.Ordinal)) continue;

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
            if (candidates.Count > 0) return PickIPAddress(candidates);
            throw new OrleansException("Failed to get a local IP address.");
        }

        /// <summary>
        /// Prints the DataConnectionString,
        /// without disclosing any credential info
        /// such as the Azure Storage AccountKey, SqlServer password or AWS SecretKey.
        /// </summary>
        /// <param name="connectionString">The connection string to print.</param>
        /// <returns>The string representation of the DataConnectionString with account credential info redacted.</returns>
        public static string RedactConnectionStringInfo(string connectionString)
        {
            string[] secretKeys =
            {
                "AccountKey=",                              // Azure Storage
                "SharedAccessSignature=",                   // Many Azure services
                "SharedAccessKey=", "SharedSecretValue=",   // ServiceBus
                "Password=",                                // SQL
                "SecretKey=", "SessionToken=",              // DynamoDb
            };
            var mark = "<--SNIP-->";
            if (String.IsNullOrEmpty(connectionString)) return "null";
            //if connection string format doesn't contain any secretKey, then return just <--SNIP-->
            if (!secretKeys.Any(key => connectionString.Contains(key))) return mark;

            string connectionInfo = connectionString;

            // Remove any secret keys from connection string info written to log files
            foreach (var secretKey in secretKeys)
            {
                int keyPos = connectionInfo.IndexOf(secretKey, StringComparison.OrdinalIgnoreCase);
                if (keyPos >= 0)
                {
                    connectionInfo = connectionInfo.Remove(keyPos + secretKey.Length) + mark;
                }
            }

            return connectionInfo;
        }
    }
}