using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.XPath;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime.Configuration;

namespace Orleans.TestFramework
{
    public class ConfigManager
    {
        // TEST RUN CONFIG FOR PRIVATE BUILDS AND/OR SHORT LOAD TEST
        //
        // NOTE: Change this to True is you want to do a short load-test run
        public static bool DoShortLoadTestRun = false;
        // NOTE: Change this to YOUR OWN alias in your shelve set for private builds
        public static string MyUserName = "orltests";
        //
        // ---------------------------------------------------------

        readonly string serverTemplate;
        readonly string clientTemplate;
        public ConfigManager()
        {
            this.serverTemplate = TestConfig.GetConfigFile("OrleansConfigurationTemplateForLoadTest.xml");
            this.clientTemplate = TestConfig.GetConfigFile("ClientConfigurationTemplateForLoadTest.xml");
        }
        public ConfigManager(string serverTemplate, string clientTemplate)
        {
            this.serverTemplate = serverTemplate ?? TestConfig.GetConfigFile("OrleansConfigurationTemplateForLoadTest.xml");
            this.clientTemplate = clientTemplate ?? TestConfig.GetConfigFile("ClientConfigurationTemplateForLoadTest.xml");
        }
        public void GenerateClusterConfiguration(DeploymentConfig deploymentConfig, string saveAsFile)
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(serverTemplate);
            XPathNavigator navigator = doc.CreateNavigator();
            XmlNamespaceManager nsm = new XmlNamespaceManager(navigator.NameTable);
            nsm.AddNamespace("oc", "urn:orleans");

            string primary = deploymentConfig.GetAssignedSiloMachine(deploymentConfig.Primary);

            // Set unique deployment id for this run
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:SystemStore", "DeploymentId", deploymentConfig.DeploymentId);
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:SystemStore", "ServiceId", deploymentConfig.ServiceId.ToString());

            // seed Node
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:SeedNode", "Address", primary);

            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Defaults/oc:Networking", "Address", primary);
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Defaults/oc:Networking", "Port", deploymentConfig.StartPort.ToString());

            // set networking
            var n = SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:Networking", "Subnet", deploymentConfig.Subnet);
            

            List<string> allSilos = deploymentConfig.GetAssignedSiloMachines();
            for (int i = 0; i < allSilos.Count; i++)
            {
                string siloName = allSilos[i];
                string machineName = deploymentConfig.GetAssignedSiloMachine(siloName);
                // TODO : Following code is not working somehow
                string xpath = string.Format("/oc:OrleansConfiguration/oc:Override[@Node=\"{0}\"]", siloName);
                SetXmlAttribute(doc, xpath + "/oc:Networking", "Port", string.Format("{0}", (deploymentConfig.StartPort + i)));
                SetXmlAttribute(doc, xpath + "/oc:Networking", "Address", machineName);
                
                SetXmlAttribute(doc, xpath + "/oc:ProxyingGateway", "Address", machineName);
                if (machineName == Environment.MachineName)
                {
                    var e = SetXmlAttribute(doc, xpath + "/oc:ProxyingGateway", "Port", (30000+i).ToString());
                    e.RemoveAttribute("Subnet");
                    n.RemoveAttribute("Subnet");
                }
                else
                {
                    SetXmlAttribute(doc, xpath + "/oc:Networking", "Subnet", deploymentConfig.Subnet);
                    SetXmlAttribute(doc, xpath + "/oc:ProxyingGateway", "Port", "30000");    
                }
                
            }

            if (deploymentConfig.SiloOptions != null)
            {
                if (deploymentConfig.SiloOptions.MockStreamProviderParameters != null)
                {
                    MockStreamProviderParameters theParams = deploymentConfig.SiloOptions.MockStreamProviderParameters;
                    const string xpathBase = "/oc:OrleansConfiguration/oc:Globals/oc:StreamProviders";
                    const string elementName = "Provider";
                    AddElementWithAttribute(doc, xpathBase, elementName, "Name", theParams.StreamProvider);
                    string xpath = string.Format("{0}/oc:{1}[@Name=\"{2}\"]", xpathBase, elementName, theParams.StreamProvider);
                    SetXmlAttribute(doc, xpath, "Type", "LoadTestGrains." + theParams.StreamProvider);
                    SetXmlAttribute(doc, xpath, "DeploymentId", deploymentConfig.DeploymentId);
                    SetXmlAttribute(doc, xpath, "StreamProvider", theParams.StreamProvider);
                    SetXmlAttribute(doc, xpath, "TotalQueueCount", theParams.TotalQueueCount.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "NumStreamsPerQueue", theParams.NumStreamsPerQueue.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "MessageProducer", theParams.MessageProducer);
                    SetXmlAttribute(doc, xpath, "ActivationTaskDelay", theParams.ActivationTaskDelay.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "ActivationBusyWait", theParams.ActivationBusyWait.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "AdditionalSubscribersCount", theParams.AdditionalSubscribersCount.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "EventTaskDelay", theParams.EventTaskDelay.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "EventBusyWait", theParams.EventBusyWait.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "SiloStabilizationTime", theParams.SiloStabilizationTime.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "RampUpStagger", theParams.RampUpStagger.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "SubscriptionLength", theParams.SubscriptionLength.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "StreamEventsPerSecond", theParams.StreamEventsPerSecond.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "TargetBatchesSentPerSecond", theParams.TargetBatchesSentPerSecond.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "MaxBatchesPerRequest", theParams.MaxBatchesPerRequest.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "MaxEventsPerBatch", theParams.MaxEventsPerBatch.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "EventSize", theParams.EventSize.ToString(CultureInfo.InvariantCulture));
                    SetXmlAttribute(doc, xpath, "CacheSizeKb", theParams.CacheSizeKb.ToString(CultureInfo.InvariantCulture));
                }

                if (deploymentConfig.SiloOptions.PlacementStrategyParameters != null)
                {
                    PlacementStrategyParameters theParams = deploymentConfig.SiloOptions.PlacementStrategyParameters;
                    string xpath = "/oc:OrleansConfiguration/oc:Globals/oc:PlacementStrategy";
                    if (FindElement(doc, xpath) == null) {
                        const string xpathBase = "/oc:OrleansConfiguration/oc:Globals";
                        const string elementName = "PlacementStrategy";
                        AddElementWithAttribute(doc, xpathBase, elementName, "DefaultPlacementStrategy", theParams.DefaultPlacementStrategy);
                    }

                    SetXmlAttribute(doc, xpath, "DefaultPlacementStrategy", theParams.DefaultPlacementStrategy);
                    if (theParams.DefaultPlacementStrategy == "ActivationCountBasedPlacement")
                    {
                        SetXmlAttribute(doc, xpath, "DeploymentLoadPublisherRefreshTime", theParams.DeploymentLoadPublisherRefreshTime.TotalMilliseconds + "ms");
                        SetXmlAttribute(doc, xpath, "ActivationCountBasedPlacementChooseOutOf", theParams.ActivationCountBasedPlacementChooseOutOf.ToString(CultureInfo.InvariantCulture));
                    }
                }

                if (deploymentConfig.SiloOptions.UseMockReminderTable.HasValue)
                {
                    string timeout = string.Format("{0}ms", deploymentConfig.SiloOptions.UseMockReminderTable.Value.TotalMilliseconds);
                    const string xpath = "/oc:OrleansConfiguration/oc:Globals/oc:SystemStore";
                    SetXmlAttribute(doc, xpath, "UseMockReminderTable", timeout);
                }

            }

            //finally write to the file.
            using (StreamWriter writer = new StreamWriter(saveAsFile))
            {
                XmlWriter xmlWriter = XmlWriter.Create(writer);
                //xmlWriter.Settings.Indent = true;
                //xmlWriter.Settings.NewLineChars = "\n";
                doc.Save(xmlWriter);
                writer.Flush();
            }
            
        }
        public void GenerateClientConfiguration(string siloName, DeploymentConfig deploymentConfig, string saveAsFile, string[] gateways)
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(clientTemplate);
            XPathNavigator navigator = doc.CreateNavigator();
            XmlNamespaceManager nsm = new XmlNamespaceManager(navigator.NameTable);
            nsm.AddNamespace("oc", "urn:orleans");

            // Set unique deployment id for this run
            SetXmlAttribute(doc, "/oc:ClientConfiguration/oc:SystemStore", "DeploymentId", deploymentConfig.DeploymentId);
            SetXmlAttribute(doc, "/oc:ClientConfiguration/oc:SystemStore", "ServiceId", deploymentConfig.ServiceId.ToString());

            if (null == gateways)
            {
                string primary = deploymentConfig.GetAssignedSiloMachine(deploymentConfig.Primary);
                SetXmlAttribute(doc, "/oc:ClientConfiguration/oc:Gateway", "Address", primary);
                SetXmlAttribute(doc, "/oc:ClientConfiguration/oc:Gateway", "Subnet", deploymentConfig.Subnet);
                SetXmlAttribute(doc, "/oc:ClientConfiguration/oc:Gateway", "Port", deploymentConfig.GatewayPort.ToString());
            }
            else
            {
                int i = 0;
                foreach (string gateway in gateways)
                {
                    XmlElement e;
                    if (i == 0)
                    {
                        // reuse the first one - basically replacing one in config file.
                        e = SetXmlAttribute(doc, "/oc:ClientConfiguration/oc:Gateway", "Address", gateway);
                    }
                    else
                    {
                        e = AddElementWithAttribute(doc, "/oc:ClientConfiguration", "Gateway", "Address", gateway);
                    }
                    
                    if(gateway == Environment.MachineName)
                    {
                        e.RemoveAttribute("Subnet");
                        e.SetAttribute("Port", (deploymentConfig.GatewayPort+i).ToString());    
                    }
                    else
                    {
                        e.SetAttribute("Subnet", deploymentConfig.Subnet);
                        e.SetAttribute("Port", deploymentConfig.GatewayPort.ToString());    
                    }
                    
                    i++;
                }
            }
            //finally write to the file.
            using (StreamWriter writer = new StreamWriter(saveAsFile))
            {
                XmlWriter xmlWriter = XmlWriter.Create(writer);
                //xmlWriter.Settings.Indent = true;
                //xmlWriter.Settings.NewLineChars = "\n";
                doc.Save(xmlWriter);
                writer.Flush();
            }
        }
        /// <summary>
        /// Loads the deployConfig file.
        /// </summary>
        /// <returns></returns>
        public ClientConfiguration LoadClientConfig()
        {
            ClientConfiguration clientConfig = ClientConfiguration.StandardLoad();
            //if (IsRemoteDeployment)
            //{
            //    clientConfig.Gateways.Clear();
            //    clientConfig.Gateways.Add(new IPEndPoint(
            //        ClusterConfiguration.ResolveIPAddress(
            //            GetAssignedSiloMachine("Primary"),
            //            subnet.Split('.').Select(s => (byte)int.Parse(s)).ToArray()),
            //        30000));
            //}
            return clientConfig;
        }

        /// <summary>
        /// Finds a specified element in the XPath.
        /// </summary>
        /// <returns>The element or null in case element was not found.</returns>
        public static XPathNavigator FindElement(XmlDocument doc, string elementPath, string prefix = "oc", string urn = "urn:orleans")
        {
            XPathNavigator nav = doc.CreateNavigator();
            XmlNamespaceManager nsm = new XmlNamespaceManager(nav.NameTable);
            nsm.AddNamespace(prefix, urn);

            return nav.SelectSingleNode(elementPath, nsm);
        }

        /// <summary>
        /// Sets the specified attribute on the elemnt specified XPath. 
        /// If the element is not present then a new element is added to the parent.
        /// </summary>
        /// <param name="doc">Document object.</param>
        /// <param name="elementPath">XPath to the parent.</param>
        /// <param name="elementName">Name of the element</param>
        /// <param name="attribute">Attribute to set.</param>
        /// <param name="value">Value to set.</param>
        /// <param name="prefix">The prefix for the namespace.</param>
        /// <param name="urn">The urn for the namespace.</param>
        public static XmlElement AddElementWithAttribute(XmlDocument doc, string elementPath, string elementName, string attribute, string value, string prefix = "oc", string urn = "urn:orleans")
        {
            XmlElement parent;
            XPathNavigator nav = doc.CreateNavigator();
            XmlNamespaceManager nsm = new XmlNamespaceManager(nav.NameTable);
            nsm.AddNamespace(prefix, urn);
            XPathNavigator elm = nav.SelectSingleNode(elementPath, nsm);
            // find or create the element.
            parent = (XmlElement)elm.UnderlyingObject;
            XmlElement element = doc.CreateElement(string.Empty, elementName, urn);
            var attr = doc.CreateAttribute(attribute);
            attr.Value = value;
            element.Attributes.Append(attr);
            parent.AppendChild(element);
            return element;
        }
        /// <summary>
        /// Sets the specified attribute on the elemnt specified XPath. 
        /// If the element is not present then a new element is added to the parent.
        /// </summary>
        /// <param name="doc">Document object.</param>
        /// <param name="elementPath">XPath to the element.</param>
        /// <param name="attribute">Attribute to set.</param>
        /// <param name="value">Value to set.</param>
        /// <param name="prefix">The prefix for the namespace.</param>
        /// <param name="urn">The urn for the namespace.</param>
        public static XmlElement SetXmlAttribute(XmlDocument doc, string elementPath, string attribute, string value, string prefix = "oc", string urn = "urn:orleans")
        {
            XmlElement element;
            XPathNavigator nav = doc.CreateNavigator();
            XmlNamespaceManager nsm = new XmlNamespaceManager(nav.NameTable);
            nsm.AddNamespace(prefix, urn);
            XPathNavigator elm = nav.SelectSingleNode(elementPath, nsm);
            // find or create the element.
            if (null == elm)
            {
                // element is not present so find the parent and add a new element to it.
                int i = elementPath.LastIndexOf("/");
                string parentPath = elementPath.Substring(0, i);
                XmlElement parent=null;
                var x1 = nav.SelectSingleNode(parentPath, nsm);
                if (x1 == null)
                {
                    //parent itself is missing
                    int firstBracket = parentPath.IndexOf("[");
                    int lastBracket = parentPath.LastIndexOf("]");
                    
                    //xpaths to parent and grand parent
                    string parentStr = parentPath.Substring(0, firstBracket);
                    string grandParentStr = parentStr.Substring(0, parentStr.LastIndexOf("/"));
                    
                    // extract name value pair fromState parent path
                    string attrStr = parentPath.Substring(firstBracket + 2);
                    attrStr = attrStr.Substring(0, attrStr.Length - 1);
                    string[] valueParts = attrStr.Split('=');
                    
                    // find grandparent and add parent as a child
                    XmlElement grandParent = (XmlElement)nav.SelectSingleNode(grandParentStr, nsm).UnderlyingObject;
                    var v3 = parentStr.Substring(parentStr.LastIndexOf(":") + 1);
                    parent = doc.CreateElement(string.Empty,v3, urn);
                    var attr = doc.CreateAttribute(valueParts[0]);
                    attr.Value = valueParts[1].Trim('"');
                    parent.Attributes.Append(attr);
                    grandParent.AppendChild(parent);
                }
                else
                {
                    parent = (XmlElement)nav.SelectSingleNode(parentPath, nsm).UnderlyingObject;
                }
                var v2 = elementPath.Substring(elementPath.LastIndexOf(":") + 1);
                element = doc.CreateElement(v2, urn);
                parent.AppendChild(element);
            }
            else
            {
                element = (XmlElement)elm.UnderlyingObject;
            }
            // finally set the attribute
            element.SetAttribute(attribute, value != null ? value : String.Empty);
            return element;
        }

        public string GetValueFromTemplate(string elementPath, string attributeName, bool isServer = true)
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(isServer?serverTemplate: clientTemplate );
            XPathNavigator navigator = doc.CreateNavigator();
            XmlNamespaceManager nsm = new XmlNamespaceManager(navigator.NameTable);
            nsm.AddNamespace("oc", "urn:orleans");
            XPathNavigator elm = navigator.SelectSingleNode(elementPath, nsm);
            // find or create the element.
            if (null != elm)
            {
                XmlElement element = (XmlElement)elm.UnderlyingObject;
                return element.GetAttribute(attributeName);
            }
            return null;
        }
    }

    public class TestConfig : IConfigurationSectionHandler
    {
        public string From { get; set; }
        public string To { get; set; }
        public string Host { get; set; }
        public int Port = 25;
        public string TestOutputFile { get; set; }
        public string TestConfigFile { get; set; }
        /// <summary>
        /// Build variables passed from the build 
        /// </summary>
        public Dictionary<string, string> Variables = new Dictionary<string, string>();
        /// <summary>
        /// Directory where this assembly is located while running.
        /// </summary>
        public static string RunningDir = Path.GetDirectoryName(typeof(TestConfig).Assembly.Location);
        public static string DefaulTestName = "LoadTestsOutput"; // used when the test is not run by Un it Test framework, but manualy.

        TestContext testContext;
        public TestContext TestContext 
        { 
            get {return testContext;}
            set 
            {
                testContext =  value;
                string testName;

                if (testContext != null)
                    testName = testContext.TestName;
                else
                    testName = DefaulTestName;

                TestOutputFile = Path.Combine(RunningDir, testName + ".log");
                if (File.Exists(TestOutputFile)) File.Delete(TestOutputFile);
            } 
        }

        public TestConfig()
        {
            TestConfigFile = GetConfigFile("TestConfiguration.xml", useDefault: false);
            GetBuildVariables();

            string toUserName;
            if (ConfigManager.DoShortLoadTestRun)
            {
                // If running test build using short load test, then only send results to ourselves
                toUserName = ConfigManager.MyUserName;
            }
            else if (Environment.UserName.ToLower().StartsWith("xcg"))
            {
                // We are running on TFS build server with individual config
                toUserName = "orltests";
            }
            else
            {
                // Running private build on TFS server
                toUserName = Environment.UserName;
            }

            To = toUserName + "@microsoft.com";
            From = Environment.UserName + "@microsoft.com";

            Host = "smtphost.redmond.corp.microsoft.com";
            Port = 25;
        }

        public static string GetConfigFile(string fileName, bool useDefault = true)
        {
            FileInfo info = new FileInfo(Path.Combine("TestConfiguration", Environment.UserName, fileName));
            if (!info.Exists)
            {
                if (!useDefault)
                {
                    Assert.Fail("Can not find {0} for user {1}", fileName, Environment.UserName);
                }
                info = new FileInfo(Path.Combine("TestConfiguration", fileName));
            }
            Assert.IsTrue(info.Exists, "Could not find Test Configuration file {0}", fileName);
            return info.FullName;
        }
        public static string GetSetting(string name, string defaultValue = null)
        {
            string user = Environment.UserName;
            string machine = Environment.MachineName;
            return ConfigurationManager.AppSettings[machine + "." + user + "." + name]
                        ?? (ConfigurationManager.AppSettings[machine + "." + name]
                                ?? (ConfigurationManager.AppSettings[user + "." + name]
                                        ?? (ConfigurationManager.AppSettings[name]
                                            ?? defaultValue
                                        )
                                )
                        );
        }
        public object GetSection(string name)
        {
            return ConfigurationManager.GetSection(name);
        }
        public object Create(object parent, object configContext, XmlNode section)
        {
            throw new NotImplementedException();
        }
        public string LocateBinariesDirectoryAtRuntime()
        {
            // For now just copy files to ..\LocalSilo
            string dir = Path.GetDirectoryName(RunningDir);

            while (Path.GetPathRoot(dir) != Path.GetDirectoryName(dir))
            {
                bool matches = false;
                // go .. until we find the directory named TestResults
                if (dir.EndsWith("TestResults")) // running under TFS or from Binaries
                {
                    // go one level up and then to Binaries
                    dir = Path.Combine(Path.GetDirectoryName(dir), "Binaries");
                    matches = true;
                }
                else if (dir.EndsWith("Code")) // running from solution 
                {
                    var binariesDir = Path.Combine(dir, "Binaries"); // for TFS builds
                    if (Directory.Exists(binariesDir))
                        dir = binariesDir;
                    else
                        dir = Path.Combine(dir, "Bin"); // for local builds
                    matches = true;
                }
                if (matches)
                {
                    if (!Directory.Exists(dir))
                    {
                        dir = Path.GetDirectoryName(Path.GetDirectoryName(dir)); // skip Binaries and go one level up
                        continue;
                    }
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            Assert.IsTrue(Directory.Exists(dir), "Could not find the Binary drop and/or path not specified correctly.");
            return dir;
        }
        public ParserGrammar GetGrammar(string name)
        {
            ParserGrammar grammar = new ParserGrammar();
            grammar.Name = name;

            XmlDocument doc = new XmlDocument();
            doc.Load(TestConfigFile);
            XPathNavigator nav = doc.CreateNavigator();
            XmlNamespaceManager nsm = new XmlNamespaceManager(nav.NameTable);
            nsm.AddNamespace("oc", "urn:orleans");
            XPathNavigator elm = nav.SelectSingleNode(string.Format("/oc:TestConfiguration/oc:QuickParser[@Name=\"{0}\"]",name), nsm);
            XmlElement quickParser = (XmlElement)elm.UnderlyingObject;
            foreach(XmlElement transitions in quickParser.GetElementsByTagName("Transition","urn:orleans"))
            {
                string tname = transitions.GetAttribute("Name");
                string from = transitions.GetAttribute("From");
                string to = transitions.GetAttribute("To");
                string pattern = transitions.GetAttribute("Pattern");
                int count = int.Parse(transitions.GetAttribute("Count"));
                var value = transitions.GetAttribute("Consecutive");
                bool consecutive = string.IsNullOrWhiteSpace(value) ? false : bool.Parse(value);
                //Name="Rule1" From="Started" To="WarmedUp" Pattern="Current TPS:" Count="10"
                grammar.AddTransitionPattern(tname, from, to, pattern, count, consecutive);
            }
            foreach (XmlElement lexer in quickParser.GetElementsByTagName("Lexer", "urn:orleans"))
            {
                List<string> vars = new List<string>();
                //Name="Pattern1" State="Stable" To="Stable" Pattern="Current TPS:(?'tps' \\d+.\\d+)"
                string lname = lexer.GetAttribute("Name");
                string state = lexer.GetAttribute("State");
                var value = lexer.GetAttribute("AutoVariables");
                bool autoVariables = string.IsNullOrWhiteSpace(value) ? false : bool.Parse(value);
                string pattern = lexer.GetAttribute("Pattern");
                pattern = pattern.Replace("{", "<").Replace("}", ">");

                foreach (XmlElement var in quickParser.GetElementsByTagName("Variable", "urn:orleans"))
                {
                    vars.Add(var.GetAttribute("Name"));
                }
                //Name="Rule1" From="Started" To="WarmedUp" Pattern="Current TPS:" Count="10"
                grammar.AddParsingPattern(lname, state, pattern, autoVariables, vars.ToArray());
            }
            return grammar;
        }

        public DeploymentConfig GetDeploymentConfig(string sectionName, string clusterName)
        {
            Log.WriteLine(SEV.INFO, "GetDeploymentConfig", "Using test deployment config file {0}", TestConfigFile);
            DeploymentConfig deployConfig = new DeploymentConfig();
            deployConfig.Name = sectionName;

            // Create unique deployment id to avoid possible side effects/ picking up bad state.
            deployConfig.DeploymentId = "loadtests" + DateTime.Now.Ticks;
            deployConfig.ServiceId = Guid.NewGuid();
           
            XmlDocument doc = new XmlDocument();
            doc.Load(TestConfigFile);
            XPathNavigator nav = doc.CreateNavigator();
            XmlNamespaceManager nsm = new XmlNamespaceManager(nav.NameTable);
            nsm.AddNamespace("oc", "urn:orleans");
            XPathNavigator elm = nav.SelectSingleNode(string.Format("/oc:TestConfiguration/oc:Deployment[@Name=\"{0}\"]", sectionName), nsm);
            if (elm == null) throw new Exception("Cannot find config section for Deployment=" + sectionName);
            XmlElement deploymentRoot = (XmlElement)elm.UnderlyingObject;
            XPathNavigator melm = nav.SelectSingleNode(string.Format("/oc:TestConfiguration/oc:Cluster[@Name=\"{0}\"]", clusterName), nsm);
            if (melm == null) throw new Exception("Cannot find config section for Cluster=" + clusterName);
            XmlElement clusterRoot = (XmlElement)melm.UnderlyingObject;
            //<Deployment Name="LoadTest">
              //  <Servers Prefix="xcg-azure-0" Start="1" End="3"/>
              //  <Clients Prefix="xcg-azure-0" Start="1" End="3"/>
              //  <SDK Path="C:\SDK-DROP"/>
              //  <ClientApp Path="C:\XCG Workspaces\Active\Checkout3\Code\Prototype\Test\LoadTests\LoadTestClient\bin\Debug\LoadTestClient.exe"/>
              //  <!--<Application Name="" Path=""/>-->
              //  <Networking Subnet="172.31" StartPort="11111" GatewayPort="30000"/>
              //</Deployment>
            //set servers
            deployConfig.AllowMachineReuse = string.IsNullOrWhiteSpace(clusterRoot.GetAttribute("AllowMachineReuse")) ? false : bool.Parse(clusterRoot.GetAttribute("AllowMachineReuse"));
            
            bool addLocalMachine = true;
            foreach (XmlElement machines in clusterRoot.GetElementsByTagName("Servers", "urn:orleans"))
            {
                addLocalMachine = false;
                string prefix = machines.GetAttribute("Prefix");
                int start = int.Parse(machines.GetAttribute("Start"));
                int end = int.Parse(machines.GetAttribute("End"));
                string skips = machines.GetAttribute("Skip");
                int[] arr = string.IsNullOrWhiteSpace(skips) ? null : (from s in skips.Split(',') select int.Parse(s)).ToArray();
                deployConfig.SetServerMachines(DeploymentConfig.GetMachines(prefix, start, end, arr));
            }
            if (addLocalMachine) deployConfig.ServerMachines.Add(Environment.MachineName);
            //set clients
            addLocalMachine = true;
            foreach (XmlElement machines in clusterRoot.GetElementsByTagName("Clients", "urn:orleans"))
            {
                addLocalMachine = false;
                string prefix = machines.GetAttribute("Prefix");
                int start = int.Parse(machines.GetAttribute("Start"));
                int end = int.Parse(machines.GetAttribute("End"));
                string skips = machines.GetAttribute("Skip");
                int[] arr = string.IsNullOrWhiteSpace(skips) ? null : (from s in skips.Split(',') select int.Parse(s)).ToArray();
                deployConfig.SetClientMachines(DeploymentConfig.GetMachines(prefix, start, end, arr));
            }
            if (addLocalMachine) deployConfig.ClientMachines.Add(Environment.MachineName);
            // Set packages
            string binariesAtRunTime = LocateBinariesDirectoryAtRuntime();
            foreach (XmlElement packagesRoot in deploymentRoot.GetElementsByTagName("Packages", "urn:orleans"))
            {
                string root = Evaluate(packagesRoot.GetAttribute("Root"));
                if(string.IsNullOrWhiteSpace(root))
                {
                    root = binariesAtRunTime;
                }
                Assert.IsTrue(Path.IsPathRooted(root), "Packages Root must be a non-relative path");
                foreach (XmlElement package in packagesRoot.GetElementsByTagName("Package", "urn:orleans"))
                {
                    string type = package.GetAttribute("Type");
                    string name = package.GetAttribute("Name");
                    string path = Evaluate(package.GetAttribute("Path"));
                    if(!Path.IsPathRooted(path))
                    {
                        path = Path.Combine(root, path);
                    }
                    
                    switch (type)
                    { 
                        case "SDK":
                            deployConfig.SdkDropPath = path;
                            break;
                        case "ClientApp":
                            deployConfig.Clients.Add(name, path);
                            break;
                        case "Application": 
                            deployConfig.Applications.Add(name, path);
                            break;
                        case "Logs":
                            deployConfig.TestLogs = path;
                            break;
                        case "SiloConfig":
                            deployConfig.ServerConfigTemplate = path;
                            break;
                        case "ClientConfig":
                            deployConfig.ClientConfigTemplate = path;
                            break;
                        case "System": break;
                    }
                    
                }
            }


            // Set networking
            foreach (XmlElement networking in clusterRoot.GetElementsByTagName("Networking", "urn:orleans"))
            {
                //<Networking Subnet="172.31" StartPort="11111" GatewayPort="30000"/>
                deployConfig.Subnet = networking.GetAttribute("Subnet");
                deployConfig.StartPort = int.Parse(networking.GetAttribute("StartPort"));
                deployConfig.GatewayPort = int.Parse(networking.GetAttribute("GatewayPort"));
            }
            return deployConfig;
        }

        public MetricCollector GetMetricCollector(string sectionName)
        {
            MetricCollector collector = new MetricCollector();
            collector.Name = sectionName;
            
            XmlDocument doc = new XmlDocument();
            doc.Load(TestConfigFile);
            XPathNavigator nav = doc.CreateNavigator();
            XmlNamespaceManager nsm = new XmlNamespaceManager(nav.NameTable);
            nsm.AddNamespace("oc", "urn:orleans");
            XPathNavigator elm = nav.SelectSingleNode(string.Format("/oc:TestConfiguration/oc:MetricCollector[@Name=\"{0}\"]", sectionName), nsm);
            XmlElement metricsRoot = (XmlElement)elm.UnderlyingObject;
            MetricCollector.EarlyResultCount = string.IsNullOrWhiteSpace(metricsRoot.GetAttribute("EarlyResultCount")) ? 40 : int.Parse(metricsRoot.GetAttribute("EarlyResultCount"));
            collector.ExitEarly = string.IsNullOrWhiteSpace(metricsRoot.GetAttribute("ExitEarly")) ? false : bool.Parse(metricsRoot.GetAttribute("ExitEarly"));
              //<MetricCollector Name="LoadTest">
              //  <Metric Type="MovingAverageMetric" Name="average_TPS" BasedOn="tps" WindowSize="10" IsGlobal="true"/>
              //  <MetricAssert Type="MetricAssert" BasedOn="average_TPS" LowWatermark="0" HighWatermark="100000" WindowSize="10" IsGlobal="true"/>
              //</MetricCollector>
            //set Metrics
            foreach (XmlElement metric in metricsRoot.GetElementsByTagName("Metric", "urn:orleans"))
            {
                string type = metric.GetAttribute("Type");
                string pass = metric.GetAttribute("Pass");
                switch (type)
                { 
                    case "AverageMetric" :
                    case "Average":
                        AverageMetric m = new AverageMetric();
                        m.Name = metric.GetAttribute("Name");
                        m.BasedOn = metric.GetAttribute("BasedOn");
                        m.Scope = metric.GetAttribute("Scope");
                        m.WindowSize = int.Parse(metric.GetAttribute("WindowSize"));
                        m.Pass = string.IsNullOrEmpty(pass) ? 0: int.Parse(pass);
                        collector.Metrics.Add(m);
                        break;
                    case "PercentileMetric":
                    case "Percentile":
                        PercentileMetric p = new PercentileMetric();
                        p.Name = metric.GetAttribute("Name");
                        p.BasedOn = metric.GetAttribute("BasedOn");
                        p.WindowSize = int.Parse(metric.GetAttribute("WindowSize"));
                        p.Scope = metric.GetAttribute("Scope");
                        p.Percentile = double.Parse(metric.GetAttribute("Percentile"));
                        p.Pass = string.IsNullOrEmpty(pass) ? 0: int.Parse(pass);
                        collector.Metrics.Add(p);
                        break;

                    case "AggregateMetric":
                    case "Aggregate":
                        AggregateMetric a = new AggregateMetric();
                        a.Name = metric.GetAttribute("Name");
                        a.BasedOn = metric.GetAttribute("BasedOn");
                        a.WindowSize = int.Parse(metric.GetAttribute("WindowSize"));
                        a.Scope = metric.GetAttribute("Scope");
                        a.Pass = string.IsNullOrEmpty(pass) ? 0: int.Parse(pass);
                        collector.Metrics.Add(a);
                        break;

                    case "MinMetric":
                    case "Min":
                        MinMetric min = new MinMetric();
                        min.Name = metric.GetAttribute("Name");
                        min.BasedOn = metric.GetAttribute("BasedOn");
                        min.WindowSize = int.Parse(metric.GetAttribute("WindowSize"));
                        min.Scope = metric.GetAttribute("Scope");
                        min.Pass = string.IsNullOrEmpty(pass) ? 0 : int.Parse(pass);
                        collector.Metrics.Add(min);
                        break;

                    case "MaxMetric":
                    case "Max":
                        MaxMetric max = new MaxMetric();
                        max.Name = metric.GetAttribute("Name");
                        max.BasedOn = metric.GetAttribute("BasedOn");
                        max.WindowSize = int.Parse(metric.GetAttribute("WindowSize"));
                        max.Scope = metric.GetAttribute("Scope");
                        max.Pass = string.IsNullOrEmpty(pass) ? 0 : int.Parse(pass);
                        collector.Metrics.Add(max);
                        break;
                    case "CountMetric":
                    case "Count":
                        CountMetric count = new CountMetric();
                        count.Name = metric.GetAttribute("Name");
                        count.BasedOn = metric.GetAttribute("BasedOn");
                        count.WindowSize = int.Parse(metric.GetAttribute("WindowSize"));
                        count.Scope = metric.GetAttribute("Scope");
                        count.Pass = string.IsNullOrEmpty(pass) ? 0 : int.Parse(pass);
                        collector.Metrics.Add(count);
                        break;
                }
            }
            //set Asserts
            foreach (XmlElement assertelm in metricsRoot.GetElementsByTagName("MetricAssert", "urn:orleans"))
            {
                string type = assertelm.GetAttribute("Type");
                switch (type)
                {
                    case "MetricWatermarkAssert":
                        MetricWatermarkAssert assert = new MetricWatermarkAssert();
                        assert.BasedOn = assertelm.GetAttribute("BasedOn");
                        assert.LowWatermark = double.Parse(assertelm.GetAttribute("LowWatermark") ?? "0");
                        assert.HighWatermark = double.Parse(assertelm.GetAttribute("HighWatermark")??double.MaxValue.ToString());;
                        assert.WindowSize = int.Parse(assertelm.GetAttribute("WindowSize"));
                        assert.IsGlobal = bool.Parse(assertelm.GetAttribute("IsGlobal").ToLower());
                        assert.ScaleBy = assertelm.GetAttribute("ScaleBy");
                        collector.Asserts.Add(assert);
                        break;
                }
            }
            return collector;
        }
        public string Evaluate(string str)
        {
            StringBuilder ret = new StringBuilder(str);
            if (!string.IsNullOrWhiteSpace(str))
            {
                foreach (string var in Variables.Keys)
                {
                    ret.Replace("$(" + var + ")", Variables[var]);
                }
            }
            return ret.ToString();
        }
        private void GetBuildVariables()
        {
            // The variables are loaded from the file BuildVariables.xml, which is in turn generated as part of build.
            // The build variables are NOT automatically imported/written to this file.
            // The line to write build variable you are interested must be EXPLICITLY added to in project file.
            XmlDocument doc = new XmlDocument();
            doc.Load(Path.Combine("TestConfiguration", "BuildVariables.xml"));
            foreach (XmlElement var in doc.GetElementsByTagName("var"))
            {
                string name = var.GetAttribute("name");
                string value = var.GetAttribute("value");
                this.Variables.Add(name,value);
            }
        }
    }
}
