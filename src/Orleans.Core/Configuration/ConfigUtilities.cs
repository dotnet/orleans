using Orleans.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Utilities class for handling configuration.
    /// </summary>
    public static class ConfigUtilities
    {
        internal static void ParseAdditionalAssemblyDirectories(IDictionary<string, SearchOption> directories, XmlElement root)
        {
            foreach (var node in root.ChildNodes)
            {
                var grandchild = node as XmlElement;

                if (grandchild == null)
                {
                    continue;
                }
                else
                {
                    if (!grandchild.HasAttribute("Path"))
                        throw new FormatException("Missing 'Path' attribute on Directory element.");

                    // default to recursive
                    var recursive = true;

                    if (grandchild.HasAttribute("IncludeSubFolders"))
                    {
                        if (!bool.TryParse(grandchild.Attributes["IncludeSubFolders"].Value, out recursive))
                            throw new FormatException("Attribute 'IncludeSubFolders' has invalid value.");

                        directories[grandchild.Attributes["Path"].Value] = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    }
                }
            }
        }

        internal static void ParseTelemetry(XmlElement root, TelemetryConfiguration telemetryConfiguration)
        {
            foreach (var node in root.ChildNodes)
            {
                var grandchild = node as XmlElement;
                if (grandchild == null) continue;

                if (!grandchild.LocalName.Equals("TelemetryConsumer"))
                {
                    continue;
                }
                else
                {
                    string typeName = grandchild.Attributes["Type"]?.Value;
                    string assemblyName = grandchild.Attributes["Assembly"]?.Value;

                    if (string.IsNullOrWhiteSpace(typeName))
                        throw new FormatException("Missing 'Type' attribute on TelemetryConsumer element.");

                    if (string.IsNullOrWhiteSpace(assemblyName))
                        throw new FormatException("Missing 'Assembly' attribute on TelemetryConsumer element.");

                    var args = grandchild.Attributes.OfType<XmlAttribute>().Where(a => a.LocalName != "Type" && a.LocalName != "Assembly")
                        .Select(x => new KeyValuePair<string, object>(x.Name, x.Value)).ToArray();

                    telemetryConfiguration.Add(typeName, assemblyName, args);
                }
            }
        }

        internal static bool TryParsePropagateActivityId(XmlElement root, string nodeName, out bool propagateActivityId)
        {
            //set default value to make compiler happy, progateActivityId is only used when this method return true
            propagateActivityId = MessagingOptions.DEFAULT_PROPAGATE_E2E_ACTIVITY_ID;
            if (root.HasAttribute("PropagateActivityId"))
            {
                propagateActivityId = ParseBool(root.GetAttribute("PropagateActivityId"),
                    "Invalid boolean value for PropagateActivityId attribute on Tracing element for " + nodeName);
                return true;
            }
            return false;
        }

        internal static void ParseStatistics(IStatisticsConfiguration config, XmlElement root, string nodeName)
        {
            if (root.HasAttribute("PerfCounterWriteInterval"))
            {
                config.StatisticsPerfCountersWriteInterval = ParseTimeSpan(root.GetAttribute("PerfCounterWriteInterval"),
                    "Invalid TimeSpan value for Statistics.PerfCounterWriteInterval attribute on Statistics element for " + nodeName);
            }
            if (root.HasAttribute("LogWriteInterval"))
            {
                config.StatisticsLogWriteInterval = ParseTimeSpan(root.GetAttribute("LogWriteInterval"),
                    "Invalid TimeSpan value for Statistics.LogWriteInterval attribute on Statistics element for " + nodeName);
            }
            if (root.HasAttribute("StatisticsCollectionLevel"))
            {
                config.StatisticsCollectionLevel = ConfigUtilities.ParseEnum<StatisticsLevel>(root.GetAttribute("StatisticsCollectionLevel"),
                    "Invalid value of for Statistics.StatisticsCollectionLevel attribute on Statistics element for " + nodeName);
            }
        }

        internal static void ParseLimitValues(LimitManager limitManager, XmlElement root, string nodeName)
        {
            foreach (XmlNode node in root.ChildNodes)
            {
                var grandchild = node as XmlElement;
                if (grandchild == null) continue;

                if (grandchild.LocalName.Equals("Limit") && grandchild.HasAttribute("Name")
                    && (grandchild.HasAttribute("SoftLimit") || grandchild.HasAttribute("HardLimit")))
                {
                    var limitName = grandchild.GetAttribute("Name");
                    limitManager.AddLimitValue(limitName, new LimitValue
                    {
                        Name = limitName,
                        SoftLimitThreshold = ParseInt(grandchild.GetAttribute("SoftLimit"),
                            "Invalid integer value for the SoftLimit attribute on the Limit element"),
                        HardLimitThreshold = grandchild.HasAttribute("HardLimit") ? ParseInt(grandchild.GetAttribute("HardLimit"),
                            "Invalid integer value for the HardLimit attribute on the Limit element") : 0,
                    });
                }
            }
        }

        internal static int ParseInt(string input, string errorMessage)
        {
            int p;
            if (!Int32.TryParse(input, out p))
            {
                throw new FormatException(errorMessage);
            }
            return p;
        }

        internal static long ParseLong(string input, string errorMessage)
        {
            long p;
            if (!Int64.TryParse(input, out p))
            {
                throw new FormatException(errorMessage + ". Tried to parse " + input);
            }
            return p;
        }

        internal static bool ParseBool(string input, string errorMessage)
        {
            bool p;
            if (Boolean.TryParse(input, out p)) return p;

            switch (input)
            {
                case "0":
                    p = false;
                    break;

                case "1":
                    p = true;
                    break;

                default:
                    throw new FormatException(errorMessage + ". Tried to parse " + input);
            }
            return p;
        }

        internal static double ParseDouble(string input, string errorMessage)
        {
            double p;
            if (!Double.TryParse(input, out p))
            {
                throw new FormatException(errorMessage + ". Tried to parse " + input);
            }
            return p;
        }

        internal static Guid ParseGuid(string input, string errorMessage)
        {
            Guid p;
            if (!Guid.TryParse(input, out p))
            {
                throw new FormatException(errorMessage);
            }
            return p;
        }

        internal static Type ParseFullyQualifiedType(string input, string errorMessage)
        {
            Type returnValue;
            try
            {
                returnValue = Type.GetType(input);
            }
            catch (Exception e)
            {
                throw new FormatException(errorMessage, e);
            }

            if (returnValue == null)
            {
                throw new FormatException(errorMessage);
            }

            return returnValue;
        }

        internal static void ValidateSerializationProvider(TypeInfo type)
        {
            if (type.IsClass == false)
            {
                throw new FormatException(string.Format("The serialization provider type {0} was not a class", type.FullName));
            }

            if (type.IsAbstract)
            {
                throw new FormatException(string.Format("The serialization provider type {0} was an abstract class", type.FullName));
            }

            if (type.IsPublic == false)
            {
                throw new FormatException(string.Format("The serialization provider type {0} is not public", type.FullName));
            }

            if (type.IsGenericType && type.IsConstructedGenericType() == false)
            {
                throw new FormatException(string.Format("The serialization provider type {0} is generic and has a missing type parameter specification", type.FullName));
            }

            var constructor = type.GetConstructor(Type.EmptyTypes);
            if (constructor == null)
            {
                throw new FormatException(string.Format("The serialization provider type {0} does not have a parameterless constructor", type.FullName));
            }

            if (constructor.IsPublic == false)
            {
                throw new FormatException(string.Format("The serialization provider type {0} has a non-public parameterless constructor", type.FullName));
            }
        }

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
            else if (trimmedInput.EndsWith("s", StringComparison.Ordinal))
            {
                unitSize = 1000 * 10000;
                numberInput = trimmedInput.Remove(trimmedInput.Length - 1).Trim();
            }
            else if (trimmedInput.EndsWith("m", StringComparison.Ordinal))
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

        internal static string ToParseableTimeSpan(TimeSpan input)
        {
            return $"{input.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)}ms";
        }

        internal static byte[] ParseSubnet(string input, string errorMessage)
        {
            return string.IsNullOrEmpty(input) ? null : input.Split('.').Select(s => (byte)ParseInt(s, errorMessage)).ToArray();
        }

        internal static T ParseEnum<T>(string input, string errorMessage)
            where T : struct // really, where T : enum, but there's no way to require that in C#
        {
            T s;
            if (!Enum.TryParse<T>(input, out s))
            {
                throw new FormatException(errorMessage + ". Tried to parse " + input);
            }
            return s;
        }

        internal static Severity ParseSeverity(string input, string errorMessage)
        {
            Severity s;
            if (!Enum.TryParse<Severity>(input, out s))
            {
                throw new FormatException(errorMessage + ". Tried to parse " + input);
            }
            return s;
        }

        internal static async Task<IPEndPoint> ParseIPEndPoint(XmlElement root, byte[] subnet = null)
        {
            if (!root.HasAttribute("Address")) throw new FormatException("Missing Address attribute for " + root.LocalName + " element");
            if (!root.HasAttribute("Port")) throw new FormatException("Missing Port attribute for " + root.LocalName + " element");

            var family = AddressFamily.InterNetwork;
            if (root.HasAttribute("Subnet"))
            {
                subnet = ParseSubnet(root.GetAttribute("Subnet"), "Invalid subnet");
            }
            if (root.HasAttribute("PreferredFamily"))
            {
                family = ParseEnum<AddressFamily>(root.GetAttribute("PreferredFamily"),
                    "Invalid preferred addressing family for " + root.LocalName + " element");
            }
            IPAddress addr = await ResolveIPAddress(root.GetAttribute("Address"), subnet, family);
            int port = ParseInt(root.GetAttribute("Port"), "Invalid Port attribute for " + root.LocalName + " element");
            return new IPEndPoint(addr, port);
        }

        internal static async Task<IPAddress> ResolveIPAddress(string addrOrHost, byte[] subnet, AddressFamily family)
        {
            var loopback = family == AddressFamily.InterNetwork ? IPAddress.Loopback : IPAddress.IPv6Loopback;
            IList<IPAddress> nodeIps;

            // if the address is an empty string, just enumerate all ip addresses available
            // on this node
            if (string.IsNullOrEmpty(addrOrHost))
            {
                nodeIps = NetworkInterface.GetAllNetworkInterfaces()
                            .SelectMany(iface => iface.GetIPProperties().UnicastAddresses)
                            .Select(addr => addr.Address)
                            .Where(addr => addr.AddressFamily == family && !IPAddress.IsLoopback(addr))
                            .ToList();
            }
            else
            {
                // Fix StreamFilteringTests_SMS tests
                if (addrOrHost.Equals("loopback", StringComparison.OrdinalIgnoreCase))
                {
                    return loopback;
                }

                // check if addrOrHost is a valid IP address including loopback (127.0.0.0/8, ::1) and any (0.0.0.0/0, ::) addresses
                IPAddress address;
                if (IPAddress.TryParse(addrOrHost, out address))
                {
                    return address;
                }

                // Get IP address from DNS. If addrOrHost is localhost will 
                // return loopback IPv4 address (or IPv4 and IPv6 addresses if OS is supported IPv6)
                nodeIps = await Dns.GetHostAddressesAsync(addrOrHost);
            }

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
            if (candidates.Count > 0)
            {
                return PickIPAddress(candidates);
            }
            var subnetStr = Utils.EnumerableToString(subnet, null, ".", false);
            throw new ArgumentException("Hostname '" + addrOrHost + "' with subnet " + subnetStr + " and family " + family + " is not a valid IP address or DNS name");
        }

        internal static IPAddress PickIPAddress(IReadOnlyList<IPAddress> candidates)
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

        internal static string IStatisticsConfigurationToString(IStatisticsConfiguration config)
        {
            var sb = new StringBuilder();
            sb.Append("   Statistics: ").AppendLine();
            sb.Append("     PerfCounterWriteInterval: ").Append(config.StatisticsPerfCountersWriteInterval).AppendLine();
            sb.Append("     LogWriteInterval: ").Append(config.StatisticsLogWriteInterval).AppendLine();
            sb.Append("     StatisticsCollectionLevel: ").Append(config.StatisticsCollectionLevel).AppendLine();
#if TRACK_DETAILED_STATS
            sb.Append("     TRACK_DETAILED_STATS: true").AppendLine();
#endif
            return sb.ToString();
        }

        /// <summary>
        /// Prints the the DataConnectionString,
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

        public static TimeSpan ParseCollectionAgeLimit(XmlElement xmlElement)
        {
            if (xmlElement.LocalName != "Deactivation")
                throw new ArgumentException("The XML element must be a <Deactivate/> element.");
            if (!xmlElement.HasAttribute("AgeLimit"))
                throw new ArgumentException("The AgeLimit attribute is required for a <Deactivate/> element.");
            return ParseTimeSpan(xmlElement.GetAttribute("AgeLimit"), "Invalid TimeSpan value for Deactivation.AgeLimit");
        }

        private static readonly string[] defaultClientConfigFileNames = { "ClientConfiguration.xml", "OrleansClientConfiguration.xml", "Client.config", "Client.xml" };

        private static readonly string[] defaultSiloConfigFileNames = { "OrleansConfiguration.xml", "orleans.config", "config.xml", "orleans.config.xml" };

        private static readonly string[] defaultConfigDirs =
        {
            null, // Will be filled in with directory location for this executing assembly
            "approot", // Azure AppRoot directory
            ".", // Current directory
            ".." // Parent directory
        };

        public static string FindConfigFile(bool isSilo)
        {
            // Add directory containing Orleans binaries to the search locations for config files
            defaultConfigDirs[0] = Path.GetDirectoryName(typeof(ConfigUtilities).GetTypeInfo().Assembly.Location);

            var notFound = new List<string>();
            foreach (string dir in defaultConfigDirs)
            {
                foreach (string file in isSilo ? defaultSiloConfigFileNames : defaultClientConfigFileNames)
                {
                    var fileName = Path.GetFullPath(Path.Combine(dir, file));
                    if (File.Exists(fileName)) return fileName;

                    notFound.Add(fileName);
                }
            }

            var whereWeLooked = new StringBuilder();
            whereWeLooked.AppendFormat("Cannot locate Orleans {0} config file.", isSilo ? "silo" : "client").AppendLine();
            whereWeLooked.AppendLine("Searched locations:");
            foreach (var i in notFound)
            {
                whereWeLooked.AppendFormat("\t- {0}", i).AppendLine();
            }

            throw new FileNotFoundException(whereWeLooked.ToString());
        }

        /// <summary>
        /// Returns the Runtime Version information.
        /// </summary>
        /// <returns>the Runtime Version information</returns>
        public static string RuntimeVersionInfo()
        {
            var sb = new StringBuilder();
            sb.Append("   Orleans version: ").AppendLine(RuntimeVersion.Current);
            sb.Append("   .NET version: ").AppendLine(Environment.Version.ToString());
            sb.Append("   OS version: ").AppendLine(Environment.OSVersion.ToString());
#if BUILD_FLAVOR_LEGACY
            sb.Append("   App config file: ").AppendLine(AppDomain.CurrentDomain.SetupInformation.ConfigurationFile); 
#endif
            sb.AppendFormat("   GC Type={0} GCLatencyMode={1}",
                              GCSettings.IsServerGC ? "Server" : "Client",
                              Enum.GetName(typeof(GCLatencyMode), GCSettings.LatencyMode))
                .AppendLine();
            return sb.ToString();
        }

        // returns true if lhs is "less" (in some repeatable sense) than rhs
        private static bool CompareIPAddresses(IPAddress lhs, IPAddress rhs)
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
}