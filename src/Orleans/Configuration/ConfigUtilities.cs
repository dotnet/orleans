/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Runtime;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Utilities class for handling configuration.
    /// </summary>
    public static class ConfigUtilities
    {
        internal static void ParseTracing(ITraceConfiguration config, XmlElement root, string nodeName)
        {
            if (root.HasAttribute("DefaultTraceLevel"))
            {
                config.DefaultTraceLevel = ParseSeverity(root.GetAttribute("DefaultTraceLevel"),
                    "Invalid trace level DefaultTraceLevel attribute value on Tracing element for " + nodeName);
            }
            if (root.HasAttribute("TraceToConsole"))
            {
                config.TraceToConsole = ParseBool(root.GetAttribute("TraceToConsole"),
                    "Invalid boolean value for TraceToConsole attribute on Tracing element for " + nodeName);
            }
            if (root.HasAttribute("TraceToFile"))
            {
                config.TraceFilePattern = root.GetAttribute("TraceToFile");
            }
            if (root.HasAttribute("WriteMessagingTraces"))
            {
                config.WriteMessagingTraces = ParseBool(root.GetAttribute("WriteMessagingTraces"),
                    "Invalid boolean value for WriteMessagingTraces attribute on Tracing element for " + nodeName);
            }
            if (root.HasAttribute("LargeMessageWarningThreshold"))
            {
                config.LargeMessageWarningThreshold = ParseInt(root.GetAttribute("LargeMessageWarningThreshold"),
                    "Invalid boolean value for LargeMessageWarningThresholdattribute on Tracing element for " + nodeName);
            }
            if (root.HasAttribute("PropagateActivityId"))
            {
                config.PropagateActivityId = ParseBool(root.GetAttribute("PropagateActivityId"),
                    "Invalid boolean value for PropagateActivityId attribute on Tracing element for " + nodeName);
            }
            if (root.HasAttribute("BulkMessageLimit"))
            {
                config.BulkMessageLimit = ParseInt(root.GetAttribute("BulkMessageLimit"),
                    "Invalid int value for BulkMessageLimit attribute on Tracing element for " + nodeName);
            }

            foreach (XmlNode node in root.ChildNodes)
            {
                var grandchild = node as XmlElement;
                if (grandchild == null) continue;

                if (grandchild.LocalName.Equals("TraceLevelOverride") && grandchild.HasAttribute("TraceLevel") && grandchild.HasAttribute("LogPrefix"))
                {
                    config.TraceLevelOverrides.Add(new Tuple<string, Logger.Severity>(grandchild.GetAttribute("LogPrefix"),
                        ParseSeverity(grandchild.GetAttribute("TraceLevel"),
                            "Invalid trace level TraceLevel attribute value on TraceLevelOverride element for " + nodeName + " prefix " +
                            grandchild.GetAttribute("LogPrefix"))));
                }
                else if (grandchild.LocalName.Equals("LogConsumer"))
                {
                    var className = grandchild.InnerText;
                    Assembly assembly = null;
                    try
                    {
                        int pos = className.IndexOf(',');
                        if (pos > 0)
                        {
                            var assemblyName = className.Substring(pos + 1).Trim();
                            className = className.Substring(0, pos).Trim();
                            assembly = Assembly.Load(assemblyName);
                        }
                        else
                        {
                            assembly = Assembly.GetExecutingAssembly();
                        }
                        var plugin = assembly.CreateInstance(className);
                        if (plugin == null) throw new TypeLoadException("Cannot locate plugin class " + className + " in assembly " + assembly.FullName);

                        if (plugin is ILogConsumer)
                        {
                            TraceLogger.LogConsumers.Add(plugin as ILogConsumer);
                        }
                        else
                        {
                            throw new InvalidCastException("LogConsumer class " + className + " must implement Orleans.ILogConsumer interface");
                        }
                    }
                    catch (Exception exc)
                    {
                        throw new TypeLoadException("Cannot load LogConsumer class " + className + " from assembly " + assembly + " - Error=" + exc);
                    }
                }
            }

            SetTraceFileName(config, nodeName, DateTime.UtcNow);
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

        internal static void ParseLimitValues(ILimitsConfiguration config, XmlElement root, string nodeName)
        {
            foreach (XmlNode node in root.ChildNodes)
            {
                var grandchild = node as XmlElement;
                if (grandchild == null) continue;

                if (grandchild.LocalName.Equals("Limit") && grandchild.HasAttribute("Name")
                    && (grandchild.HasAttribute("SoftLimit") || grandchild.HasAttribute("HardLimit")))
                {
                    var limitName = grandchild.GetAttribute("Name");
                    config.LimitValues.Add(limitName, new LimitValue
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

        internal static void SetTraceFileName(ITraceConfiguration config, string nodeName, DateTime timestamp)
        {
            const string dateFormat = "yyyy-MM-dd-HH.mm.ss.fffZ";

            if (config == null) throw new ArgumentNullException("config");

            if (config.TraceFilePattern == null
                || string.IsNullOrWhiteSpace(config.TraceFilePattern)
                || config.TraceFilePattern.Equals("false", StringComparison.OrdinalIgnoreCase)
                || config.TraceFilePattern.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                config.TraceFileName = null;
            }
            else if (string.Empty.Equals(config.TraceFileName))
            {
                config.TraceFileName = null; // normalize
            }
            else
            {
                string traceFileDir = null;
                string traceFileName = Path.GetFileName(config.TraceFilePattern);
                string[] dirLocations = { Path.GetDirectoryName(config.TraceFilePattern), "appdir", "." };
                foreach (var d in dirLocations)
                {
                    if (!Directory.Exists(d)) continue;

                    traceFileDir = d;
                    break;
                }
                if (traceFileDir != null && !Directory.Exists(traceFileDir))
                {
                    config.TraceFilePattern = Path.Combine(traceFileDir, traceFileName);
                }
                config.TraceFileName = String.Format(config.TraceFilePattern, nodeName, timestamp.ToUniversalTime().ToString(dateFormat), Dns.GetHostName());
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

        // Time spans are entered as a string of decimal digits, optionally followed by a unit string: "ms", "s", "m", "hr"
        internal static TimeSpan ParseTimeSpan(string input, string errorMessage)
        {
            int unitSize;
            string numberInput;
            var trimmedInput = input.Trim().ToLower(CultureInfo.InvariantCulture);
            if (trimmedInput.EndsWith("ms", StringComparison.Ordinal))
            {
                unitSize = 1;
                numberInput = trimmedInput.Remove(trimmedInput.Length - 2).Trim();
            }
            else if (trimmedInput.EndsWith("s", StringComparison.Ordinal))
            {
                unitSize = 1000;
                numberInput = trimmedInput.Remove(trimmedInput.Length - 1).Trim();
            }
            else if (trimmedInput.EndsWith("m", StringComparison.Ordinal))
            {
                unitSize = 60 * 1000;
                numberInput = trimmedInput.Remove(trimmedInput.Length - 1).Trim();
            }
            else if (trimmedInput.EndsWith("hr", StringComparison.Ordinal))
            {
                unitSize = 60 * 60 * 1000;
                numberInput = trimmedInput.Remove(trimmedInput.Length - 2).Trim();
            }
            else
            {
                unitSize = 1000; // Default is seconds
                numberInput = trimmedInput;
            }
            double rawTimeSpan;
            if (!double.TryParse(numberInput, out rawTimeSpan))
            {
                throw new FormatException(errorMessage + ". Tried to parse " + input);
            }
            return TimeSpan.FromMilliseconds(rawTimeSpan * unitSize);
        }

        internal static byte[] ParseSubnet(string input, string errorMessage)
        {
            return string.IsNullOrEmpty(input) ? null : input.Split('.').Select(s => (byte) ParseInt(s, errorMessage)).ToArray();
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

        internal static Logger.Severity ParseSeverity(string input, string errorMessage)
        {
            Logger.Severity s;
            if (!Enum.TryParse<Logger.Severity>(input, out s))
            {
                throw new FormatException(errorMessage + ". Tried to parse " + input);
            }
            return s;
        }

        internal static IPEndPoint ParseIPEndPoint(XmlElement root, byte[] subnet = null)
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
            IPAddress addr = ClusterConfiguration.ResolveIPAddress(root.GetAttribute("Address"), subnet, family);
            int port = ParseInt(root.GetAttribute("Port"), "Invalid Port attribute for " + root.LocalName + " element");
            return new IPEndPoint(addr, port);
        }

        internal static string TraceConfigurationToString(ITraceConfiguration config)
        {
            var sb = new StringBuilder();
            sb.Append("   Tracing: ").AppendLine();
            sb.Append("     Default Trace Level: ").Append(config.DefaultTraceLevel).AppendLine();
            if (config.TraceLevelOverrides.Count > 0)
            {
                sb.Append("     TraceLevelOverrides:").AppendLine();
                foreach (var over in config.TraceLevelOverrides)
                {
                    sb.Append("         ").Append(over.Item1).Append(" ==> ").Append(over.Item2.ToString()).AppendLine();
                }
            }
            else
            {
                sb.Append("     TraceLevelOverrides: None").AppendLine();
            }
            sb.Append("     Trace to Console: ").Append(config.TraceToConsole).AppendLine();
            sb.Append("     Trace File Name: ").Append(string.IsNullOrWhiteSpace(config.TraceFileName) ? "" : Path.GetFullPath(config.TraceFileName)).AppendLine();
            sb.Append("     Write Messaging Traces: ").Append(config.WriteMessagingTraces).AppendLine();
            sb.Append("     LargeMessageWarningThreshold: ").Append(config.LargeMessageWarningThreshold).AppendLine();
            sb.Append("     PropagateActivityId: ").Append(config.PropagateActivityId).AppendLine();
            sb.Append("     BulkMessageLimit: ").Append(config.BulkMessageLimit).AppendLine();
            return sb.ToString();
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
        /// such as the Azure Storage AccountKey or SqlServer password.
        /// </summary>
        /// <param name="dataConnectionString">The connection string to print.</param>
        /// <returns>The string representation of the DataConnectionString with account credential info redacted.</returns>
        public static string RedactConnectionStringInfo(string dataConnectionString)
        {
            return PrintSqlConnectionString(
                PrintDataConnectionInfo(dataConnectionString));
        }

        public static string PrintDataConnectionInfo(string azureConnectionString)
        {
            if (String.IsNullOrEmpty(azureConnectionString)) return "null";

            string azureConnectionInfo = azureConnectionString;
            // Remove any Azure account keys from connection string info written to log files
            int accountKeyPos = azureConnectionInfo.LastIndexOf("AccountKey=", StringComparison.Ordinal);
            if (accountKeyPos > 0)
            {
                azureConnectionInfo = azureConnectionInfo.Remove(accountKeyPos) + "AccountKey=<--SNIP-->";
            }
            return azureConnectionInfo;
        }

        public static string PrintSqlConnectionString(string sqlConnectionString)
        {
            if (String.IsNullOrEmpty(sqlConnectionString))
            {
                return "null";
            }
            var sqlConnectionInfo = sqlConnectionString;
            // Remove any Azure account keys from connection string info written to log files
            int keyPos = sqlConnectionInfo.LastIndexOf("Password=", StringComparison.OrdinalIgnoreCase);
            if (keyPos > 0)
            {
                sqlConnectionInfo = sqlConnectionInfo.Remove(keyPos) + "Password=<--SNIP-->";
            }
            return sqlConnectionInfo;
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
            defaultConfigDirs[0] = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

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
            sb.Append("   .NET version: ").AppendLine(Environment.Version.ToString());
            sb.Append("   Is .NET 4.5=").AppendLine(IsNet45OrNewer().ToString());
            sb.Append("   OS version: ").AppendLine(Environment.OSVersion.ToString());
            sb.AppendFormat("   GC Type={0} GCLatencyMode={1}",
                              GCSettings.IsServerGC ? "Server" : "Client",
                              Enum.GetName(typeof(GCLatencyMode), GCSettings.LatencyMode))
                .AppendLine();
            return sb.ToString();
        }

        internal static bool IsNet45OrNewer()
        {
            // From: http://stackoverflow.com/questions/8517159/how-to-detect-at-runtime-that-net-version-4-5-currently-running-your-code

            // Class "ReflectionContext" exists from .NET 4.5 onwards.
            return Type.GetType("System.Reflection.ReflectionContext", false) != null;
        }
    }
}
