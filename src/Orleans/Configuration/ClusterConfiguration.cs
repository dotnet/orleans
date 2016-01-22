using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.IO;
using System.Xml;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Threading.Tasks;


namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Data object holding Silo configuration parameters.
    /// </summary>
    [Serializable]
    public class ClusterConfiguration
    {
        /// <summary>
        /// The global configuration parameters that apply uniformly to all silos.
        /// </summary>
        public GlobalConfiguration Globals { get; private set; }

        /// <summary>
        /// The default configuration parameters that apply to each and every silo. 
        /// These can be over-written on a per silo basis.
        /// </summary>
        public NodeConfiguration Defaults { get; private set; }

        /// <summary>
        /// The configuration file.
        /// </summary>
        public string SourceFile { get; private set; }

        private IPEndPoint primaryNode;
        /// <summary>
        /// The Primary Node IP and port (in dev setting).
        /// </summary>
        public IPEndPoint PrimaryNode { get { return primaryNode; } set { SetPrimaryNode(value); } }

        /// <summary>
        /// Per silo configuration parameters overrides.
        /// </summary>
        public IDictionary<string, NodeConfiguration> Overrides { get; private set; }

        private Dictionary<string, string> overrideXml;
        private readonly Dictionary<string, List<Action>> listeners = new Dictionary<string, List<Action>>();
        internal bool IsRunningAsUnitTest { get; set; }

        /// <summary>
        /// ClusterConfiguration constructor.
        /// </summary>
        public ClusterConfiguration()
        {
            Init();
        }

        /// <summary>
        /// ClusterConfiguration constructor.
        /// </summary>
        public ClusterConfiguration(TextReader input)
        {
            Load(input);
        }

        private void Init()
        {
            Globals = new GlobalConfiguration();
            Defaults = new NodeConfiguration();
            Overrides = new Dictionary<string, NodeConfiguration>();
            overrideXml = new Dictionary<string, string>();
            SourceFile = "";
            IsRunningAsUnitTest = false;
        }

        /// <summary>
        /// Loads configuration from a given input text reader.
        /// </summary>
        /// <param name="input">The TextReader to use.</param>
        public void Load(TextReader input)
        {
            Init();

            LoadFromXml(ParseXml(input));
        }

        internal void LoadFromXml(XmlElement root)
        {
            foreach (XmlNode c in root.ChildNodes)
            {
                var child = c as XmlElement;
                if (child == null) continue; // Skip comment lines

                switch (child.LocalName)
                {
                    case "Globals":
                        Globals.Load(child);
                        // set subnets so this is independent of order
                        Defaults.Subnet = Globals.Subnet;
                        foreach (var o in Overrides.Values)
                        {
                            o.Subnet = Globals.Subnet;
                        }
                        if (Globals.SeedNodes.Count > 0)
                        {
                            primaryNode = Globals.SeedNodes[0];
                        }
                        break;
                    case "Defaults":
                        Defaults.Load(child);
                        Defaults.Subnet = Globals.Subnet;
                        break;
                    case "Override":
                        overrideXml[child.GetAttribute("Node")] = WriteXml(child);
                        break;
                }
            }
            CalculateOverrides();
        }

        private static string WriteXml(XmlElement element)
        {
            using(var sw = new StringWriter())
            {
                using(var xw = XmlWriter.Create(sw))
                { 
                    element.WriteTo(xw);
                    xw.Flush();
                    return sw.ToString();
                }
            }
        }

        private void CalculateOverrides()
        {
            if (Globals.LivenessEnabled &&
                Globals.LivenessType == GlobalConfiguration.LivenessProviderType.NotSpecified)
            {
                if (Globals.UseSqlSystemStore)
                {
                    Globals.LivenessType = GlobalConfiguration.LivenessProviderType.SqlServer;
                }
                else if (Globals.UseAzureSystemStore)
                {
                    Globals.LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable;
                }
                else if (Globals.UseZooKeeperSystemStore)
                {
                    Globals.LivenessType = GlobalConfiguration.LivenessProviderType.ZooKeeper;
                }
                else
                {
                    Globals.LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain;
                }
            }

            if (Globals.UseMockReminderTable)
            {
                Globals.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.MockTable);
            }
            else if (Globals.ReminderServiceType == GlobalConfiguration.ReminderServiceProviderType.NotSpecified)
            {
                if (Globals.UseSqlSystemStore)
                {
                    Globals.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.SqlServer);
                }
                else if (Globals.UseAzureSystemStore)
                {
                    Globals.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.AzureTable);
                }
                else if (Globals.UseZooKeeperSystemStore)
                {
                    Globals.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.Disabled);
                }
                else
                {
                    Globals.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain);
                }
            }
            
            foreach (var p in overrideXml)
            {
                var n = new NodeConfiguration(Defaults);
                n.Load(ParseXml(new StringReader(p.Value)));
                InitNodeSettingsFromGlobals(n);
                Overrides[n.SiloName] = n;
            }
        }

        private void InitNodeSettingsFromGlobals(NodeConfiguration n)
        {
            if (n.Endpoint.Equals(this.PrimaryNode)) n.IsPrimaryNode = true;
            if (Globals.SeedNodes.Contains(n.Endpoint)) n.IsSeedNode = true;
        }

        public void LoadFromFile(string fileName)
        {
            using (TextReader input = File.OpenText(fileName))
            {
                Load(input);
                SourceFile = fileName;
            }
        }

        /// <summary>
        /// Returns the configuration for a given silo.
        /// </summary>
        /// <param name="name">Silo name.</param>
        /// <returns>NodeConfiguration associated with the specified silo.</returns>
        public NodeConfiguration GetConfigurationForNode(string name)
        {
            NodeConfiguration n;
            if (Overrides.TryGetValue(name, out n)) return n;

            n = new NodeConfiguration(Defaults) {SiloName = name};
            InitNodeSettingsFromGlobals(n);
            Overrides[name] = n;
            return n;
        }

        private void SetPrimaryNode(IPEndPoint primary)
        {
            primaryNode = primary;
            foreach (NodeConfiguration node in Overrides.Values)
            {
                if (node.Endpoint.Equals(primary))
                {
                    node.IsPrimaryNode = true;
                }
            }
        }

        /// <summary>
        /// Loads the configuration from the standard paths
        /// </summary>
        /// <returns></returns>
        public void StandardLoad()
        {
            string fileName = ConfigUtilities.FindConfigFile(true); // Throws FileNotFoundException
            LoadFromFile(fileName);
        }

        /// <summary>
        /// Subset of XML configuration file that is updatable at runtime
        /// </summary>
        private static readonly XmlElement updatableXml = ParseXml(new StringReader(@"
        <OrleansConfiguration>
            <Globals>
                <Messaging ResponseTimeout=""?""/>
                <Caching CacheSize=""?""/>
                <Liveness ProbeTimeout=""?"" TableRefreshTimeout=""?"" NumMissedProbesLimit=""?""/>
            </Globals>
            <Defaults>
                <LoadShedding Enabled=""?"" LoadLimit=""?""/>
                <Tracing DefaultTraceLevel=""?"" PropagateActivityId=""?"">
                    <TraceLevelOverride LogPrefix=""?"" TraceLevel=""?""/>
                </Tracing>
            </Defaults>
        </OrleansConfiguration>"));

        /// <summary>
        /// Updates existing configuration.
        /// </summary>
        /// <param name="input">The input string in XML format to use to update the existing configuration.</param>
        /// <returns></returns>
        public void Update(string input)
        {
            var xml = ParseXml(new StringReader(input));
            var disallowed = new List<string>();
            CheckSubtree(updatableXml, xml, "", disallowed);
            if (disallowed.Count > 0)
                throw new ArgumentException("Cannot update configuration with" + disallowed.ToStrings());
            var dict = ToChildDictionary(xml);
            XmlElement globals;
            if (dict.TryGetValue("Globals", out globals))
            {
                Globals.Load(globals);
                ConfigChanged("Globals");
                foreach (var key in ToChildDictionary(globals).Keys)
                {
                    ConfigChanged("Globals/" + key);
                }
            }
            XmlElement defaults;
            if (dict.TryGetValue("Defaults", out defaults))
            {
                Defaults.Load(defaults);
                CalculateOverrides();
                ConfigChanged("Defaults");
                foreach (var key in ToChildDictionary(defaults).Keys)
                {
                    ConfigChanged("Defaults/" + key);
                }
            }
        }

        private static void CheckSubtree(XmlElement allowed, XmlElement test, string prefix, List<string> disallowed)
        {
            prefix = prefix + "/" + test.LocalName;
            if (allowed.LocalName != test.LocalName)
            {
                disallowed.Add(prefix);
                return;
            }
            foreach (var attribute in AttributeNames(test))
            {
                if (! allowed.HasAttribute(attribute))
                {
                    disallowed.Add(prefix + "/@" + attribute);
                }
            }
            var allowedChildren = ToChildDictionary(allowed);
            foreach (var t in test.ChildNodes)
            {
                var testChild = t as XmlElement;
                if (testChild == null)
                    continue;
                XmlElement allowedChild;
                if (! allowedChildren.TryGetValue(testChild.LocalName, out allowedChild))
                {
                    disallowed.Add(prefix + "/" + testChild.LocalName);
                }
                else
                {
                    CheckSubtree(allowedChild, testChild, prefix, disallowed);
                }
            }
        }

        private static Dictionary<string, XmlElement> ToChildDictionary(XmlElement xml)
        {
            var result = new Dictionary<string, XmlElement>();
            foreach (var c in xml.ChildNodes)
            {
                var child = c as XmlElement;
                if (child == null)
                    continue;
                result[child.LocalName] = child;
            }
            return result;
        }

        private static IEnumerable<string> AttributeNames(XmlElement element)
        {
            foreach (var a in element.Attributes)
            {
                var attr = a as XmlAttribute;
                if (attr != null)
                    yield return attr.LocalName;
            }
        }

        internal void OnConfigChange(string path, Action action, bool invokeNow = true)
        {
            List<Action> list;
            if (listeners.TryGetValue(path, out list))
                list.Add(action);
            else
                listeners.Add(path, new List<Action> { action });
            if (invokeNow)
                action();
        }

        internal void ConfigChanged(string path)
        {
            List<Action> list;
            if (!listeners.TryGetValue(path, out list)) return;

            foreach (var action in list)
                action();
        }

        /// <summary>
        /// Prints the current config for a given silo.
        /// </summary>
        /// <param name="siloName">The name of the silo to print its configuration.</param>
        /// <returns></returns>
        public string ToString(string siloName)
        {
            var sb = new StringBuilder();
            sb.Append("Config File Name: ").AppendLine(string.IsNullOrEmpty(SourceFile) ? "" : Path.GetFullPath(SourceFile));
            sb.Append("Host: ").AppendLine(Dns.GetHostName());
            sb.Append("Start time: ").AppendLine(TraceLogger.PrintDate(DateTime.UtcNow));
            sb.Append("Primary node: ").AppendLine(PrimaryNode == null ? "null" : PrimaryNode.ToString());
            sb.AppendLine("Platform version info:").Append(ConfigUtilities.RuntimeVersionInfo());
            sb.AppendLine("Global configuration:").Append(Globals.ToString());
            NodeConfiguration nc = GetConfigurationForNode(siloName);
            sb.AppendLine("Silo configuration:").Append(nc.ToString());
            sb.AppendLine();
            return sb.ToString();
        }

        internal static async Task<IPAddress> ResolveIPAddress(string addrOrHost, byte[] subnet, AddressFamily family)
        {
            var loopback = (family == AddressFamily.InterNetwork) ? IPAddress.Loopback : IPAddress.IPv6Loopback;

            if (addrOrHost.Equals("loopback", StringComparison.OrdinalIgnoreCase) ||
                addrOrHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                addrOrHost.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return loopback;
            }
            else if (addrOrHost == "0.0.0.0")
            {
                return IPAddress.Any;
            }
            else
            {
                // IF the address is an empty string, default to the local machine, but not the loopback address
                if (String.IsNullOrEmpty(addrOrHost))
                {
                    addrOrHost = Dns.GetHostName();

                    // If for some reason we get "localhost" back. This seems to have happened to somebody.
                    if (addrOrHost.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                        return loopback;
                }

                var candidates = new List<IPAddress>();
                IPAddress[] nodeIps = await Dns.GetHostAddressesAsync(addrOrHost);
                foreach (var nodeIp in nodeIps)
                {
                    if (nodeIp.AddressFamily != family || nodeIp.Equals(loopback)) continue;

                    // If the subnet does not match - we can't resolve this address.
                    // If subnet is not specified - pick smallest address deterministically.
                    if (subnet == null)
                    {
                        candidates.Add(nodeIp);
                    }
                    else
                    {
                        IPAddress ip = nodeIp;
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
                    if(CompareIPAddresses(addr, chosen)) // pick smallest address deterministically
                        chosen = addr;
                }
            }
            return chosen;
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
            for (int i=0; i < netInterfaces.Length; i++)
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
                        if(!(isLoopbackInterface && ip.Address.Equals(loopback)))
                        {
                            candidates.Add(ip.Address); // collect all candidates.
                        }
                    }
                }
            }
            if (candidates.Count > 0) return PickIPAddress(candidates);
            throw new OrleansException("Failed to get a local IP address.");
        }

        private static XmlElement ParseXml(TextReader input)
        {
            var doc = new XmlDocument();
            var xmlReader = XmlReader.Create(input);
            doc.Load(xmlReader);
            return doc.DocumentElement;
        }
    }
}
