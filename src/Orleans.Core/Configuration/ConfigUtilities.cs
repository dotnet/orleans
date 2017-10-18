using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
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
            propagateActivityId = Constants.DEFAULT_PROPAGATE_E2E_ACTIVITY_ID;
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
            if (root.HasAttribute("ProviderType"))
            {
                config.StatisticsProviderName = root.GetAttribute("ProviderType");
            }
            if (root.HasAttribute("MetricsTableWriteInterval"))
            {
                config.StatisticsMetricsTableWriteInterval = ParseTimeSpan(root.GetAttribute("MetricsTableWriteInterval"),
                    "Invalid TimeSpan value for Statistics.MetricsTableWriteInterval attribute on Statistics element for " + nodeName);
            }
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
            if (root.HasAttribute("WriteLogStatisticsToTable"))
            {
                config.StatisticsWriteLogStatisticsToTable = ParseBool(root.GetAttribute("WriteLogStatisticsToTable"),
                    "Invalid bool value for Statistics.WriteLogStatisticsToTable attribute on Statistics element for " + nodeName);
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
            IPAddress addr = await ClusterConfiguration.ResolveIPAddress(root.GetAttribute("Address"), subnet, family);
            int port = ParseInt(root.GetAttribute("Port"), "Invalid Port attribute for " + root.LocalName + " element");
            return new IPEndPoint(addr, port);
        }

        internal static string IStatisticsConfigurationToString(IStatisticsConfiguration config)
        {
            var sb = new StringBuilder();
            sb.Append("   Statistics: ").AppendLine();
            sb.Append("     MetricsTableWriteInterval: ").Append(config.StatisticsMetricsTableWriteInterval).AppendLine();
            sb.Append("     PerfCounterWriteInterval: ").Append(config.StatisticsPerfCountersWriteInterval).AppendLine();
            sb.Append("     LogWriteInterval: ").Append(config.StatisticsLogWriteInterval).AppendLine();
            sb.Append("     WriteLogStatisticsToTable: ").Append(config.StatisticsWriteLogStatisticsToTable).AppendLine();
            sb.Append("     StatisticsCollectionLevel: ").Append(config.StatisticsCollectionLevel).AppendLine();
#if TRACK_DETAILED_STATS
            sb.Append("     TRACK_DETAILED_STATS: true").AppendLine();
#endif
            if (!string.IsNullOrEmpty(config.StatisticsProviderName))
                sb.Append("     StatisticsProviderName:").Append(config.StatisticsProviderName).AppendLine();
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

            if (String.IsNullOrEmpty(connectionString)) return "null";

            string connectionInfo = connectionString;

            // Remove any secret keys from connection string info written to log files
            foreach (var secretKey in secretKeys)
            {
                int keyPos = connectionInfo.IndexOf(secretKey, StringComparison.OrdinalIgnoreCase);
                if (keyPos >= 0)
                {
                    connectionInfo = connectionInfo.Remove(keyPos + secretKey.Length) + "<--SNIP-->";
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
    }
}