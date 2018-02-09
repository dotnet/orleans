using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml;


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
        public IPEndPoint PrimaryNode { get { return this.primaryNode; } set { SetPrimaryNode(value); } }

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
            this.Globals = new GlobalConfiguration();
            this.Defaults = new NodeConfiguration();
            this.Overrides = new Dictionary<string, NodeConfiguration>();
            this.overrideXml = new Dictionary<string, string>();
            this.SourceFile = "";
            this.IsRunningAsUnitTest = false;
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
                        this.Globals.Load(child);
                        // set subnets so this is independent of order
                        this.Defaults.Subnet = this.Globals.Subnet;
                        foreach (var o in this.Overrides.Values)
                        {
                            o.Subnet = this.Globals.Subnet;
                        }
                        if (this.Globals.SeedNodes.Count > 0)
                        {
                            this.primaryNode = this.Globals.SeedNodes[0];
                        }
                        break;
                    case "Defaults":
                        this.Defaults.Load(child);
                        this.Defaults.Subnet = this.Globals.Subnet;
                        break;
                    case "Override":
                        this.overrideXml[child.GetAttribute("Node")] = WriteXml(child);
                        break;
                }
            }
            CalculateOverrides();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        private static string WriteXml(XmlElement element)
        {
            using (var sw = new StringWriter())
            {
                using (var xw = XmlWriter.Create(sw))
                {
                    element.WriteTo(xw);
                    xw.Flush();
                    return sw.ToString();
                }
            }
        }

        private void CalculateOverrides()
        {
            if (this.Globals.LivenessEnabled &&
                this.Globals.LivenessType == GlobalConfiguration.LivenessProviderType.NotSpecified)
            {
                if (this.Globals.UseAdoNetSystemStore)
                {
                    this.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.AdoNet;
                }
                else if (this.Globals.UseAzureSystemStore)
                {
                    this.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable;
                }
                else if (this.Globals.UseZooKeeperSystemStore)
                {
                    this.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.ZooKeeper;
                }
                else
                {
                    this.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain;
                }
            }

            if (this.Globals.UseMockReminderTable)
            {
                this.Globals.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.MockTable);
            }
            else if (this.Globals.ReminderServiceType == GlobalConfiguration.ReminderServiceProviderType.NotSpecified)
            {
                if (this.Globals.UseAdoNetSystemStore)
                {
                    this.Globals.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.AdoNet);
                }
                else if (this.Globals.UseAzureSystemStore)
                {
                    this.Globals.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.AzureTable);
                }
                else if (this.Globals.UseZooKeeperSystemStore)
                {
                    this.Globals.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.Disabled);
                }
                else
                {
                    this.Globals.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain);
                }
            }

            foreach (var p in this.overrideXml)
            {
                var n = new NodeConfiguration(this.Defaults);
                n.Load(ParseXml(new StringReader(p.Value)));
                n.InitNodeSettingsFromGlobals(this);
                this.Overrides[n.SiloName] = n;
            }
        }

        /// <summary>Loads the configuration from a file</summary>
        /// <param name="fileName">The file path.</param>
        public void LoadFromFile(string fileName)
        {
            using (TextReader input = File.OpenText(fileName))
            {
                Load(input);
                this.SourceFile = fileName;
            }
        }

        /// <summary>
        /// Obtains the configuration for a given silo.
        /// </summary>
        /// <param name="siloName">Silo name.</param>
        /// <param name="siloNode">NodeConfiguration associated with the specified silo.</param>
        /// <returns>true if node was found</returns>
        public bool TryGetNodeConfigurationForSilo(string siloName, out NodeConfiguration siloNode)
        {
            return this.Overrides.TryGetValue(siloName, out siloNode);
        }

        /// <summary>
        /// Creates a configuration node for a given silo.
        /// </summary>
        /// <param name="siloName">Silo name.</param>
        /// <returns>NodeConfiguration associated with the specified silo.</returns>
        public NodeConfiguration CreateNodeConfigurationForSilo(string siloName)
        {
            var siloNode = new NodeConfiguration(this.Defaults) { SiloName = siloName };
            siloNode.InitNodeSettingsFromGlobals(this);
            this.Overrides[siloName] = siloNode;
            return siloNode;
        }

        /// <summary>
        /// Creates a node config for the specified silo if one does not exist.  Returns existing node if one already exists
        /// </summary>
        /// <param name="siloName">Silo name.</param>
        /// <returns>NodeConfiguration associated with the specified silo.</returns>
        public NodeConfiguration GetOrCreateNodeConfigurationForSilo(string siloName)
        {
            NodeConfiguration siloNode;
            return !TryGetNodeConfigurationForSilo(siloName, out siloNode) ? CreateNodeConfigurationForSilo(siloName) : siloNode;
        }

        private void SetPrimaryNode(IPEndPoint primary)
        {
            this.primaryNode = primary;
            foreach (NodeConfiguration node in this.Overrides.Values)
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
        private static readonly XmlElement UpdatableXml = ParseXml(new StringReader(@"
        <OrleansConfiguration>
            <Globals>
                <Messaging ResponseTimeout=""?""/>
                <Caching CacheSize=""?""/>
                <Liveness ProbeTimeout=""?"" TableRefreshTimeout=""?"" NumMissedProbesLimit=""?""/>
            </Globals>
            <Defaults>
                <LoadShedding Enabled=""?"" LoadLimit=""?""/>
                <Tracing PropagateActivityId=""?"">
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
            CheckSubtree(UpdatableXml, xml, "", disallowed);
            if (disallowed.Count > 0)
                throw new ArgumentException("Cannot update configuration with" + disallowed.ToStrings());
            var dict = ToChildDictionary(xml);
            XmlElement globals;
            if (dict.TryGetValue("Globals", out globals))
            {
                this.Globals.Load(globals);
                ConfigChanged("Globals");
                foreach (var key in ToChildDictionary(globals).Keys)
                {
                    ConfigChanged("Globals/" + key);
                }
            }
            XmlElement defaults;
            if (dict.TryGetValue("Defaults", out defaults))
            {
                this.Defaults.Load(defaults);
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
                if (!allowed.HasAttribute(attribute))
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
                if (!allowedChildren.TryGetValue(testChild.LocalName, out allowedChild))
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
            if (this.listeners.TryGetValue(path, out list))
                list.Add(action);
            else
                this.listeners.Add(path, new List<Action> { action });
            if (invokeNow)
                action();
        }

        internal void ConfigChanged(string path)
        {
            List<Action> list;
            if (!this.listeners.TryGetValue(path, out list)) return;

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
            sb.Append("Config File Name: ").AppendLine(string.IsNullOrEmpty(this.SourceFile) ? "" : Path.GetFullPath(this.SourceFile));
            sb.Append("Host: ").AppendLine(Dns.GetHostName());
            sb.Append("Start time: ").AppendLine(LogFormatter.PrintDate(DateTime.UtcNow));
            sb.Append("Primary node: ").AppendLine(this.PrimaryNode == null ? "null" : this.PrimaryNode.ToString());
            sb.AppendLine("Platform version info:").Append(ConfigUtilities.RuntimeVersionInfo());
            sb.AppendLine("Global configuration:").Append(this.Globals.ToString());
            NodeConfiguration nc;
            if (TryGetNodeConfigurationForSilo(siloName, out nc))
            {
                sb.AppendLine("Silo configuration:").Append(nc);
            }
            sb.AppendLine();
            return sb.ToString();
        }

        private static XmlElement ParseXml(TextReader input)
        {
            var doc = new XmlDocument();
            var xmlReader = XmlReader.Create(input);
            doc.Load(xmlReader);
            return doc.DocumentElement;
        }

        /// <summary>
        /// Returns a prepopulated ClusterConfiguration object for a primary local silo (for testing)
        /// </summary>
        /// <param name="siloPort">TCP port for silo to silo communication</param>
        /// <param name="gatewayPort">Client gateway TCP port</param>
        /// <returns>ClusterConfiguration object that can be passed to Silo or SiloHost classes for initialization</returns>
        public static ClusterConfiguration LocalhostPrimarySilo(int siloPort = 22222, int gatewayPort = 40000)
        {
            var config = new ClusterConfiguration();
            var siloAddress = new IPEndPoint(IPAddress.Loopback, siloPort);
            config.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.MembershipTableGrain;
            config.Globals.SeedNodes.Add(siloAddress);
            config.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain;

            config.Defaults.HostNameOrIPAddress = "localhost";
            config.Defaults.Port = siloPort;
            config.Defaults.ProxyGatewayEndpoint = new IPEndPoint(IPAddress.Loopback, gatewayPort);

            config.PrimaryNode = siloAddress;

            return config;
        }
    }
}
