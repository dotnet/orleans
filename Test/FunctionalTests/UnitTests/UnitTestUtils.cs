using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;
using System.Threading;
using System.Reflection;
using System.Diagnostics;
using Microsoft.Concurrency.TestTools.UnitTesting.Chess;
using Orleans;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Coordination;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.RuntimeCore;
using System.Net;
using System.Globalization;
using Orleans.Runtime.Counters;
using Orleans.Scheduler;
using Orleans.Serialization;
using System.Management;
using System.Management.Automation.Runspaces;
using System.Management.Automation;
using System.IO;
using System.Configuration;
using System.Xml.XPath;

namespace UnitTests
{

    
    /// <summary>
    /// Utility class to for deploying and manging silos
    /// </summary>
    public class Deployer
    {
        /// <summary>
        /// Maps silo names to machines
        /// </summary>
        private static Dictionary<string, string> siloNamesToMachineMap = new Dictionary<string, string>();

        /// <summary>
        /// maps machine name to connection
        /// </summary>
        private static Dictionary<string, ManagementScope> wmiConnections = new Dictionary<string, ManagementScope>();

        /// <summary>
        /// Options for by unit tests.
        /// </summary>
        Options options;
        /// <summary>
        /// Options for client 
        /// </summary>
        ClientOptions clientOptions;

        /// <summary>
        /// 
        /// </summary>
        private string subnet;

        /// <summary>
        /// 
        /// </summary>
        private int startPort;
        
        /// <summary>
        /// property for used to uniquely identify the test 
        /// </summary>
        public string uniqueTestId;

        /// <summary>
        /// List of machines available for deployments
        /// </summary>
        private static string[] machines;
        
        /// <summary>
        /// Index used to keep track of machines.
        /// </summary>
        private static int currentIndex = 0;

        /// <summary>
        /// The path used to deploy on the remote machine
        /// </summary>
        private string remoteDeploymentPath;

        /// <summary>
        /// path used to pickup the 
        /// </summary>
        private string localDeploymentPath;

        /// <summary>
        /// ??
        /// </summary>
        private string sdkDropPath;

        /// <summary>
        /// Gets the name of the machine on which silo is running.
        /// </summary>
        /// <param name="siloName">Name of the silo.</param>
        /// <returns>Name of the machine.</returns>
        public string GetSiloMachine(string siloName)
        {
            // assign machine if not already assigned.
            if(!siloNamesToMachineMap.ContainsKey(siloName))
            {
                siloNamesToMachineMap.Add(siloName,machines[currentIndex++]);
            }
            return siloNamesToMachineMap[siloName];
        }
        /// <summary>
        /// Gets the cached ManagementScope for machine
        /// </summary>
        /// <param name="machine">machine name</param>
        /// <returns>returns cached object if present and connected , else creates a new one</returns>
        private static ManagementScope GetManagementScope(string machine)
        {
            ManagementScope scope;
            if(wmiConnections.ContainsKey(machine))
            {
                scope = wmiConnections[machine];
                if (scope.IsConnected)
                {
                    return scope;
                }
                else
                {
                    wmiConnections.Remove(machine);
                }
            }
            
            ConnectionOptions connOpt = new ConnectionOptions();
            connOpt.Impersonation = ImpersonationLevel.Impersonate;
            connOpt.EnablePrivileges = true;
            scope = new ManagementScope(String.Format(@"\\{0}\ROOT\CIMV2", machine), connOpt);
            scope.Connect();
            wmiConnections.Add(machine, scope);
            return scope;
        }
        /// <summary>
        /// Returns whether the remote deployment is required.
        /// </summary>
        private bool IsRemoteDeployment
        {
            get
            {
                if (!options.StartOutOfProcess) return false;
                // return true if any of the silos is not running locally.
                foreach (string siloName in siloNamesToMachineMap.Keys)
                {
                    string machine = siloNamesToMachineMap[siloName];
                    if (!(machine == "." || machine == "localhost" || machine == Environment.MachineName)) return true;
                }
                return false;
            }
        }
        
        
        
        /// <summary>
        /// Initializes the deployer.
        /// </summary>
        /// <param name="options">Unit test options.</param>
        /// <param name="clientOptions">Client options.</param>
        public void Initialize(Options options, ClientOptions clientOptions)
        {
            this.options = options;
            this.clientOptions = clientOptions;
            
            uniqueTestId = DateTime.UtcNow.ToString("d-MMM-yyyy-HH-mm");
            
            // load from the appconfig
            // initialize paths 
            remoteDeploymentPath = string.Format(@"C:\TestResults\{0}-{1}-{2}", Environment.UserName, Environment.MachineName, uniqueTestId);
            sdkDropPath = UnitTestConfig.GetSetting("SDKPath") ;
            localDeploymentPath = sdkDropPath != null ? Path.Combine(sdkDropPath,"LocalSilo") : Path.GetDirectoryName(typeof(UnitTestBase).Assembly.Location);
            
            // initialize other settings
            machines = UnitTestConfig.GetSetting("Hosts","localhost").Split(',');
            subnet  = UnitTestConfig.GetSetting("Subnet");
            startPort = int.Parse(UnitTestConfig.GetSetting("StartPort", "11111"));
            
            // kill all the processes
            if (options.StartFreshOrleans)
            {
                foreach (string machine in machines)
                {
                    try
                    {
                        foreach (Process p in Process.GetProcessesByName("OrleansHost", machine))
                        {
                            KillProcess(p);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
        
        /// <summary>
        /// Deletes the file/directory from remote machine.
        /// </summary>
        /// <param name="machineName">name of the machine</param>
        /// <param name="path">path is specified as local to machine.</param>
        public static void RemoveFromRemoteMachine(string machineName, string path)
        {
            string remotePath = @"\\" + machineName + @"\" + path.Replace(":", "$");
            if ((File.GetAttributes(remotePath) & FileAttributes.Directory) == FileAttributes.Directory)
            {
                Directory.Delete(remotePath, true);
            }
            else
            {
                File.Delete(remotePath);
            }
        }

        /// <summary>
        /// Copies from file to file or from directory to directory.
        /// The remote path is specified as if it was a local path on the remote machine.
        /// Eg. C:\MyPath on MyMachine 
        /// </summary>
        /// <param name="machineName">Name of the machine</param>
        /// <param name="from">path local to machine on which this method is running</param>
        /// <param name="to">path local to remote machine</param>
        /// <param name="exclude">list of files to exclude</param>
        public static void CopyToRemoteMachine(string machineName, string from, string to, string[] exclude = null)
        {
            if (from.Equals(to)) return;
            if ((File.GetAttributes(from) & FileAttributes.Directory) == FileAttributes.Directory)
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = @"C:\Windows\System32\xcopy.exe";
                startInfo.CreateNoWindow = true;
                startInfo.UseShellExecute = false;
                startInfo.WorkingDirectory = Path.GetDirectoryName(typeof(UnitTestBase).Assembly.Location);
                StringBuilder args = new StringBuilder();
                args.AppendFormat(" \"{0}\" \"{1}\"", from, @"\\" + machineName + @"\" + to.Replace(":", "$"));
                if (null != exclude && exclude.Length > 0)
                {
                    args.AppendFormat(" \"/EXCLUDE:{0}\"", string.Join("+", exclude));
                }
                args.AppendFormat(" /I");
                args.AppendFormat(" /V /E /Y /Z");
                startInfo.Arguments = args.ToString();
                Process xcopy = Process.Start(startInfo);
                xcopy.WaitForExit();
            }
            else
            {
                File.Copy(from, @"\\" + machineName + @"\" + to.Replace(":", "$"), true);
            }
        }
        
        /// <summary>
        /// Deploys orleans to remote machine.
        /// </summary>
        /// <param name="machineName">Name of the machine to deploy to.</param>
        public void DeployToRemoteMachine(string machineName,string siloName)
        {
            if (IsRemoteDeployment)
            {
                string destination = Path.Combine(remoteDeploymentPath, siloName);
                if (!Directory.Exists(destination))
                {
                    CopyToRemoteMachine(machineName, localDeploymentPath, destination);
                    if (string.IsNullOrEmpty(sdkDropPath))
                    {
                        CopyDependencies(machineName, destination);
                    }
                }
            }
        }

        /// <summary>
        /// Copy predefined dependencies to remote machine.
        /// </summary>
        /// <param name="machineName">name of the machine.</param>
        private void CopyDependencies(string machineName, string destination)
        {
            // TODO : fix the path
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.Location.EndsWith("FSharp.Core.dll"))
                {
                    CopyToRemoteMachine(machineName, asm.Location, Path.Combine(destination, "FSharp.Core.dll"));
                    break;
                }
            }
            try
            {
                RemoveFromRemoteMachine(machineName, Path.Combine(destination, Path.GetFileName(typeof(UnitTestBase).Assembly.Location)));
            }
            catch(Exception )
            {
            }
        }
        
        /// <summary>
        /// Starts the silo in the remote machine
        /// </summary>
        /// <param name="siloName">Name of the silo.</param>
        /// <param name="config">Configs to pass to the silo.</param>
        /// <returns></returns>
        public Process StartSiloProcess(string siloName, OrleansConfiguration config)
        {
            //save the config so that you can pass it can be passed to the OrleansHost process.
            //launch the process
            string configFileName = "UnitTestConfig" + DateTime.Now.Ticks.ToString() + ".xml";
            string configLocalPath = Path.Combine(Path.GetDirectoryName(typeof(UnitTestBase).Assembly.Location), configFileName);
            string baseLocation = IsRemoteDeployment ? Path.Combine(remoteDeploymentPath,siloName) : Path.GetDirectoryName(typeof(UnitTestBase).Assembly.Location);
            string configRemotePath = Path.Combine(baseLocation, configFileName);

            // Deploy to remote machine
            DeployToRemoteMachine(siloNamesToMachineMap[siloName],siloName);

            // generate and copy generated config file
            if (IsRemoteDeployment)
            {
                WriteRemoteServerConfigFile(siloNamesToMachineMap[siloName], configLocalPath, config);
                CopyToRemoteMachine(siloNamesToMachineMap[siloName], configLocalPath, configRemotePath);
            }
            else
            {
                WriteLocalServerConfigFile(configLocalPath, config);
            }

            // start the process
            string processName = "OrleansHost.exe";
            string imagePath = Path.Combine(baseLocation, processName);
            return StartProcess(siloNamesToMachineMap[siloName], imagePath, siloName, configRemotePath);
        }

        /// <summary>
        /// Starts a process on remote machine.
        /// </summary>
        /// <param name="machineName">Name of the machine.</param>
        /// <param name="processPath">Local path to the excutible.</param>
        /// <param name="parameters">List of parameters to pass to the process.</param>
        /// <returns></returns>
        public static Process StartProcess(string machineName, string processPath, params string[] parameters)
        {
            StringBuilder args = new StringBuilder();
            foreach (string s in parameters)
            {
                args.AppendFormat(" \"{0}\" ", s);
            }

            if (machineName == "." || machineName == "localhost")
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = processPath;
                startInfo.CreateNoWindow = true;
                startInfo.WorkingDirectory = Path.GetDirectoryName(processPath);
                startInfo.UseShellExecute = false;
                startInfo.Arguments = args.ToString();
                Process retValue = Process.Start(startInfo);
                string startupEventName = String.Format("{0}:{1}", processPath, parameters[0]).ToLowerInvariant().Replace('\\', '.');
                bool createdNew;
                EventWaitHandle startupEvent = new EventWaitHandle(false, EventResetMode.ManualReset, startupEventName, out createdNew);
                if (!createdNew) startupEvent.Reset();
                bool b = startupEvent.WaitOne(15000);
                return retValue;

            }
            else
            {
                string commandline = string.Format("{0} {1}", processPath, args);
                ManagementScope scope = GetManagementScope(machineName);
                    
                ObjectGetOptions objectGetOptions = new ObjectGetOptions();
                ManagementPath managementPath = new ManagementPath("Win32_Process");
                ManagementClass processClass = new ManagementClass(scope, managementPath, objectGetOptions);
                ManagementBaseObject inParams = processClass.GetMethodParameters("Create");
                inParams["CommandLine"] = commandline;
                inParams["CurrentDirectory"] = Path.GetDirectoryName(processPath);

                ManagementBaseObject outParams = processClass.InvokeMethod("Create", inParams, null);
                Thread.Sleep(5000);
                int processId = int.Parse(outParams["processId"].ToString());
                return Process.GetProcessById(processId, machineName);
            }
        }

        /// <summary>
        /// Kills the process.
        /// In case of remote processes may use WMI to terminate the process.
        /// </summary>
        /// <param name="process">Process to kill.</param>
        public static void KillProcess(Process process)
        {
            try
            {
                process.Kill();
            }
            catch(Exception )
            {
                if (!(process.MachineName == "." || process.MachineName == "localhost" || process.MachineName == Environment.MachineName))
                {
                    try
                    {
                        // connect
                        ManagementScope scope = GetManagementScope(process.MachineName);

                        // find the process
                        ObjectQuery query = new System.Management.ObjectQuery(string.Format("Select * from Win32_Process where ProcessId='{0}'",process.Id));
                        ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher(scope,query);
                        ManagementObjectCollection results = searcher.Get();
 
                        // Terminate the process
                        foreach (System.Management.ManagementObject remoteprocess in results)
                        {
                            string[] argList = new string[] { string.Empty };
 
                            // id of process
                            if (int.Parse(remoteprocess["ProcessId"].ToString())==process.Id)
                            {
                                object[] obj = new object[] { 0 };
                                remoteprocess.InvokeMethod("Terminate", obj);
                            }
                        }
                    }
                    catch(Exception)
                    {
                    }
                }
            }
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
        public static void SetXmlAttribute(XmlDocument doc, string elementPath, string attribute, string value, string prefix = "oc", string urn = "urn:orleans")
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
                XmlElement parent = (XmlElement)nav.SelectSingleNode(parentPath, nsm).UnderlyingObject;
                var v2= elementPath.Substring(elementPath.LastIndexOf(":")+1);
                element = (XmlElement)doc.CreateElement(v2, urn);
                parent.AppendChild(element);
            }
            else
            {
                element = (XmlElement)elm.UnderlyingObject;
            }
            // finally set the attribute
            element.SetAttribute(attribute, value != null ? value : String.Empty);
        }
        
        /// <summary>
        /// Write the server config file so that silo can be started locally.
        /// </summary>
        /// <param name="saveAsFile">Path where configs are saved.</param>
        /// <param name="config">Configs to pass to the silo.</param>
        public void WriteLocalServerConfigFile(string saveAsFile, OrleansConfiguration config)
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(Path.Combine(localDeploymentPath, "OrleansConfiguration.xml"));
            XPathNavigator navigator = doc.CreateNavigator();
            XmlNamespaceManager nsm = new XmlNamespaceManager(navigator.NameTable);
            nsm.AddNamespace("oc", "urn:orleans");

            // Placement
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Defaults/oc:Placement", "SingleActivationMode", config.Defaults.SingleActivationMode.ToString().ToLower());

            // Persistence
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:Persistence", "Type", config.Globals.StorageType.ToString());
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:Persistence", "Path", config.Globals.StorageDirectory);
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:Persistence", "QueryConnection", config.Globals.QueryConnection);
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:Persistence", "ClearQueryStore", config.Globals.ClearQueryStore.ToString().ToLower());
            
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
        /// Write the server config file so that silo can be started on a specified machine.
        /// </summary>
        /// <param name="machineName">Name of the machine.</param>
        /// <param name="saveAsFile">Path where configs are saved.</param>
        /// <param name="config">Configs to pass to the silo.</param>
        public void WriteRemoteServerConfigFile(string machineName, string saveAsFile, ClusterConfiguration config)
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(Path.Combine(localDeploymentPath, "OrleansConfiguration.xml"));
            XPathNavigator navigator = doc.CreateNavigator();
            XmlNamespaceManager nsm = new XmlNamespaceManager(navigator.NameTable);
            nsm.AddNamespace("oc", "urn:orleans");

            // Placement
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Defaults/oc:Placement", "SingleActivationMode", config.Defaults.SingleActivationMode.ToString().ToLower());

            // Persistence
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:Persistence", "Type", config.Globals.StorageType.ToString());
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:Persistence", "Path", config.Globals.StorageDirectory);
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:Persistence", "QueryConnection", config.Globals.QueryConnection);
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:Persistence", "ClearQueryStore", config.Globals.ClearQueryStore.ToString().ToLower());

            // seed Node
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:SeedNode", "Address", machineName);

            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Defaults/oc:Networking", "Address", machineName);
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Defaults/oc:Networking", "Port", startPort.ToString());

            // set networking
            SetXmlAttribute(doc, "/oc:OrleansConfiguration/oc:Globals/oc:Networking", "Subnet", subnet);

            // Based on assumption that you can have multiple overrides for the same node and the last one wins.
            XmlElement configElm = (XmlElement)navigator.SelectSingleNode("/oc:OrleansConfiguration", nsm).UnderlyingObject;
            for (int i = 0; i < 4; i++)
            {
                // TODO : Following code is not working somehow
                string name = i == 0 ? "Primary" : string.Format("Secondary_{0}", i);
                string xpath = string.Format("/oc:OrleansConfiguration/oc:Override[@Node=\"{0}\"]", name);
                SetXmlAttribute(doc, xpath + "/oc:Networking", "Port", string.Format("{0}", (startPort + i)));
                if (i == 0)
                {
                    SetXmlAttribute(doc, xpath + "/oc:ProxyingGateway", "Address", machineName);
                    SetXmlAttribute(doc, xpath + "/oc:ProxyingGateway", "Port", "30000");
                }

                //var over = doc.CreateElement("Override", "urn:orleans");
                //over.SetAttribute("Node", i == 0 ? "Primary" : string.Format("Secondary_{0}", i));

                //var networkingElmNode = doc.CreateElement("Networking", "urn:orleans");
                //networkingElmNode.SetAttribute("Port", string.Format("{0}", (startPort + i)));
                //over.AppendChild(networkingElmNode);
                //if (i == 0)
                //{
                //    var gateWay = doc.CreateElement("ProxyingGateway", "urn:orleans");
                //    gateWay.SetAttribute("Address", machineName);
                //    gateWay.SetAttribute("Port", "30000");
                //    over.AppendChild(gateWay);
                //}

                //configElm.AppendChild(over);
                
            }

            // finally write to the file.
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
        /// Loads the config file.
        /// </summary>
        /// <returns></returns>
        public ClientConfiguration LoadClientConfig()
        {
            ClientConfiguration clientConfig;
            if (IsRemoteDeployment)
            {
                string configFileName = Path.Combine(Path.GetDirectoryName(typeof(UnitTestBase).Assembly.Location),
                    "UnitTestConfigClient-" + uniqueTestId + ".xml");
                WriteClientFile(configFileName);
                clientConfig = ClientConfiguration.LoadFromFile(configFileName);
            }
            else
            {
                clientConfig = ClientConfiguration.StandardLoad();
            }
            return clientConfig;
        }

        /// <summary>
        /// Writes the client config file.
        /// </summary>
        /// <param name="saveAsFile"></param>
        public void WriteClientFile(string saveAsFile)
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(Path.Combine(localDeploymentPath, "ClientConfiguration.xml"));
            XPathNavigator navigator = doc.CreateNavigator();
            XmlNamespaceManager nsm = new XmlNamespaceManager(navigator.NameTable);
            nsm.AddNamespace("oc", "urn:orleans");

            // Placement
            SetXmlAttribute(doc, "/oc:ClientConfiguration/oc:Gateway", "Address", siloNamesToMachineMap["Primary"]);
            SetXmlAttribute(doc, "/oc:ClientConfiguration/oc:Gateway", "Subnet", subnet);

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
        /// Runs a powershell script
        /// </summary>
        /// <param name="scriptPath"></param>
        /// <param name="options"></param>
        public static void RunScript(string scriptPath, params string[] options)
        {
            Command command = new Command(scriptPath + " " + string.Join(" ", options), true);

            RunspaceConfiguration runspaceConfiguration = RunspaceConfiguration.Create();

            Runspace runspace = RunspaceFactory.CreateRunspace(runspaceConfiguration);
            runspace.Open();

            RunspaceInvoke scriptInvoker = new RunspaceInvoke(runspace);

            using (Pipeline pipeline = runspace.CreatePipeline())
            {
                pipeline.Commands.Add(command);

                try
                {
                    var results = pipeline.Invoke();
                }
                catch (Exception e)
                {
                    Console.WriteLine("Exception" + e.ToString());
                }
            }
        }

    }
   
    public class UnitTestConfig : IConfigurationSectionHandler
    {
        public static string GetSetting(string name, string defaultValue = null)
        {
            string user = Environment.UserName;
            string machine = Environment.MachineName;
            return ConfigurationManager.AppSettings[machine+"."+user+"."+name] 
                        ??(ConfigurationManager.AppSettings[machine+"."+name]  
                                ?? (ConfigurationManager.AppSettings[user+"."+name] 
                                        ??  (ConfigurationManager.AppSettings[name] 
                                            ?? defaultValue
                                        )
                                )
                        );
        }
        public object  Create(object parent, object configContext, XmlNode section)
        {
 	        throw new NotImplementedException();
        }
    }
}
