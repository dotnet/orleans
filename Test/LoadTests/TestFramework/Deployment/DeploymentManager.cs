using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Test.BabySitter;

namespace Orleans.TestFramework
{
    [Serializable]
    public class ProcessHandle
    {
        public string Name { get; set; }
        public Process Process { get; set; }
        public string MachineName { get; set; }
        public string ConfigFilePath { get; set; }
        public string LogFilePath { get; set; }
        //public List<string> AdditionalLogFilePaths = new List<string>();
        public Dictionary<string,QuickParser> ActiveListeners = new Dictionary<string,QuickParser>();
        public string ImagePath { get; set; }
        public string[] Parameters { get; set; }

        public override string ToString()
        {
            return String.Format("[{0} on {1}, Config: {2}, Log: {3}, Image: {4}]",
             Name,
             MachineName,
             ConfigFilePath,
             LogFilePath,
             ImagePath);
        }
    }

    [Serializable]
    public class SiloHandle : ProcessHandle
    {
        public string Subnet { get; set; }
        public int Port { get; set; }
        public bool IsRunning { get; set; }
    }
    [Serializable]
    public class ClientHandle : ProcessHandle
    {

    }

    public class ClientOptions
    {
        public int ServerCount { get; set; }
        public int ClientCount { get; set; }
        public int ServersPerClient { get; set; }
        public int Users { get; set; }
        public int Number { get; set; }
        public int Pipeline { get; set; }
        public int Workers { get; set; }
        public int Threads { get; set; }
        public string ClientAppName { get; set; }
        public bool UseTU1 { get; set; }
        public bool UseAzureSiloTable { get; set; }
        public bool DirectTest { get; set; }
        public string[] AdditionalParameters { get; set; }
       
        public ClientOptions()
        {
            ServersPerClient = 5;
            Users = -1;
            Number = -1; //700 * 1000;
            Pipeline = 2500;
            Workers = 1;
            Threads = 1;
            ClientAppName = "PresenceConsole";
            UseTU1 = false;
            UseAzureSiloTable = true;
            DirectTest = false;
        }

        public ClientOptions Copy()
        {
            ClientOptions copy = new ClientOptions();
            copy.ServerCount = this.ServerCount;
            copy.ClientCount = this.ClientCount;
            copy.ServersPerClient = this.ServersPerClient;
            copy.Users = this.Users;
            copy.Number = this.Number;
            copy.Pipeline = this.Pipeline;
            copy.Workers = this.Workers;
            copy.Threads = this.Threads;
            copy.ClientAppName = this.ClientAppName;
            copy.UseTU1 = this.UseTU1;
            copy.UseAzureSiloTable = this.UseAzureSiloTable;
            copy.DirectTest = this.DirectTest;
            if (AdditionalParameters != null)
            {
                // Deep copy AdditionalParameters data
                copy.AdditionalParameters = new string[this.AdditionalParameters.Length];
                this.AdditionalParameters.CopyTo(copy.AdditionalParameters, 0);
            }
            return copy;
        }

        public List<string> AsParameters()
        {
            List<string> options = new List<string>();
            // private static int NUM_USERS = 500 * 1000;
            // private static long NUM_REQUESTS = 100 * 1000 * 1000;
            if (Number != -1) { options.Add("-n"); options.Add((Number).ToString(CultureInfo.InvariantCulture)); }
            if (Users != -1) { options.Add("-u"); options.Add((Users).ToString(CultureInfo.InvariantCulture)); }
            if (Pipeline != -1) { options.Add("-p"); options.Add((Pipeline).ToString(CultureInfo.InvariantCulture)); }
            if (Workers != -1) { options.Add("-w"); options.Add((Workers).ToString(CultureInfo.InvariantCulture)); }
            if (Threads != -1) { options.Add("-t"); options.Add((Threads).ToString(CultureInfo.InvariantCulture)); }
            if (UseTU1) options.Add("-tu1");
            if (UseAzureSiloTable) options.Add("-azure");
            if (DirectTest) options.Add("-direct");

            if (AdditionalParameters != null)
            {
                foreach (var parameter in AdditionalParameters)
                {
                    options.Add(parameter);
                }
            }

            return options;
        }
    }

    public class SiloOptions
    {
        public PlacementStrategyParameters PlacementStrategyParameters { get; set; }
        public MockStreamProviderParameters MockStreamProviderParameters { get; set; }
        public TimeSpan? UseMockReminderTable { get; set; }
    }

    /// <summary>
    /// Utility class to for deploying and manging silos
    /// </summary>
    public class DeploymentManager
    {
        // for workloads with input graph file. Need to move out to config.
        public static string INPUT_GRAPH_FILE = null;

        private readonly DateTime BaseTime = DateTime.UtcNow;
        /// <summary>
        /// maps machine name to wmi connections
        /// </summary>
        private static readonly Dictionary<string, ManagementScope> wmiConnections = new Dictionary<string, ManagementScope>();
        /// <summary>
        /// maps machine name to number of wmi failures
        /// </summary>
        private static readonly Dictionary<string, int> wmiFailures = new Dictionary<string, int>();

        /// <summary>
        /// Deployment Config
        /// </summary>
        private readonly DeploymentConfig deployConfig;

        /// <summary>
        /// Configuration Manager
        /// </summary>
        private readonly ConfigManager configManager;

        /// <summary>
        /// property for used to uniquely identify the test run
        /// This is static so each run has unique id
        /// </summary>
        public static string UniqueRunId = DateTime.Now.ToString("MMM-dd-yyyy-HH-mm");
        public static string UniqueDeploymentId = string.Format(@"{0}-{1}-{2}", Environment.UserName, Environment.MachineName, UniqueRunId);

        public const string DeploymentRoot = @"C:\TestResults\";
        /// <summary>
        /// property for used to uniquely identify the test inside the run
        /// </summary>
        private static int uniqueTestId;

        /// <summary>
        /// The path used to deploy on the remote machine.
        /// This is a path underwhich silos will be copied on remote machine.
        /// @"C:\TestResults\{UserName}-{MachineName}-{uniqueRunId}"
        /// </summary>
        private readonly string remoteDeploymentPath;

        /// <summary>
        /// path from where to pickup the binaries
        /// </summary>
        private readonly string localDeploymentPath;

        /// <summary>
        /// directory where logs are saved
        /// </summary>
        private string logoutputPath;

        public string LogoutputPath
        {
            get { return logoutputPath; }
            set { logoutputPath = value; }
        }

        /// <summary>
        /// path used to generate configs
        /// </summary>
        private readonly string tempDirForConfigGen;

        /// <summary>
        /// Maps clients to processes
        /// </summary>
        private readonly Dictionary<string, ClientHandle> clientNamesToHandles = new Dictionary<string, ClientHandle>();

        private readonly List<string> logsToSave = new List<string>();

        public List<SiloHandle> Silos;
        public List<ClientHandle> Clients;
        public List<QuickParser> Logs;

        /// <summary>
        /// Gets the cached ManagementScope for machine
        /// </summary>
        /// <param name="machine">machine name</param>
        /// <returns>returns cached object if present and connected , else creates a new one</returns>
        private static ManagementScope GetManagementScope(string machine)
        {
            lock (wmiConnections)
            {
                ManagementScope scope;
                if (wmiConnections.ContainsKey(machine))
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

                ConnectionOptions connOpt = new ConnectionOptions { Impersonation = ImpersonationLevel.Impersonate, EnablePrivileges = true };
                scope = new ManagementScope(String.Format(@"\\{0}\ROOT\CIMV2", machine), connOpt);
                try
                {
                    TaskHelper.ExecuteWithTimeout(() => { scope.Connect(); }, TimeSpan.FromSeconds(120)).Wait();
                }
                catch (Exception exc)
                {
                    if (exc.GetBaseException().GetType().Equals(typeof(TimeoutException)))
                        Log.WriteLine(SEV.ERROR, "GetManagementScope", "There was a timeout in WMI connecting to {0}", machine);
                    throw exc;
                }
               
                wmiConnections.Add(machine, scope);
                wmiFailures.Add(machine,0);
                return scope;
            }
            
        }
        /// <summary>
        /// Initializes the deployer.
        /// </summary>
        public DeploymentManager(DeploymentConfig deployConfig, bool cleanup = true)
        {
            this.deployConfig = deployConfig;
            this.configManager = new ConfigManager(deployConfig.ServerConfigTemplate, deployConfig.ClientConfigTemplate);
            uniqueTestId++;

            // initialize paths 
            remoteDeploymentPath = Path.Combine(DeploymentRoot, UniqueDeploymentId);
            localDeploymentPath = Path.Combine(Path.GetDirectoryName(TestConfig.RunningDir), "LocalSilo");

            // copy silo binaries into the temp directory from where we will ultimately copy it to all silo machines
            // we copy to temp because we add additional files which are not present in sdk drop eg. babysitter, Fsharp binaries
            if (!Directory.Exists(localDeploymentPath))
            {
                CopyFiles(deployConfig.SdkDropPath, localDeploymentPath);
                CopyDependencies(localDeploymentPath);
            }
            
            // create directory for generating configs
            // effectively ..\GeneratedConfigs\
            tempDirForConfigGen = Path.Combine(Path.GetDirectoryName(TestConfig.RunningDir), "GeneratedConfigs");
            if (!Directory.Exists(tempDirForConfigGen))
            {
                try
                {
                    Directory.CreateDirectory(tempDirForConfigGen);
                }
                catch (Exception exc)
                {
                    throw new Exception("Directory.CreateDirectory " + tempDirForConfigGen + " failed with exception: " + exc.ToString(), exc);
                }
            }
            Log.WriteLine(SEV.STATUS,"Config.Init", "Generated configs stored in {0}", tempDirForConfigGen);

            // create directory for creating logs
            logoutputPath = Path.Combine(deployConfig.TestLogs, UniqueDeploymentId);
            if (!Directory.Exists(logoutputPath))
            {
                try
                {
                    Directory.CreateDirectory(logoutputPath);
                }
                catch (Exception exc)
                {
                    throw new Exception("Directory.CreateDirectory " + logoutputPath + " failed with exception: " + exc.ToString(), exc);
                }
            }
            logoutputPath = Path.Combine(logoutputPath, uniqueTestId.ToString());
            if (!Directory.Exists(logoutputPath))
            {
                try
                {
                    Directory.CreateDirectory(logoutputPath);
                }
                catch (Exception exc)
                {
                    throw new Exception("Directory.CreateDirectory " + logoutputPath + " failed with exception: " + exc.ToString(), exc);
                }
            }
            
            Log.WriteLine(SEV.STATUS, "Config.Init", "Log outputs stored in {0}", logoutputPath);

            if (cleanup)
            {
                CleanUp();
            }
        }

        public void SaveLogs()
        {
            lock (logsToSave)
            {
                foreach (string log in logsToSave)
                {
                    try
                    {
                        File.Copy(log, Path.Combine(logoutputPath, Path.GetFileName(log)));
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
        /// <summary>
        /// Cleanup zombie processes.
        /// </summary>
        public void CleanUp()
        {
            List<Task> tasks = new List<Task>(); 
            try
            {
                // Kill all the zombie Client processes
                foreach(string machine in deployConfig.ClientMachines)
                {
                    try
                    {
                        string processName = Path.GetFileNameWithoutExtension(deployConfig.ClientAppPath);
                        string machineName = machine == "localhost" ? "." : machine;
                        foreach (Process p in Process.GetProcessesByName(processName, machineName))
                        {
                            var capture = p;
                            Task t = Task.Factory.StartNew(() =>
                            {
                                KillProcess(capture);
                            });
                            tasks.Add(t);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.WriteLine(SEV.ERROR, "Deployment.Client", "Failed to stop client process on machine {0} due to exception {1}", machine, e.ToString());
                    }
                }
                // Kill all the zombie Server processes
                foreach(string machine in deployConfig.ServerMachines)
                {
                    try
                    {
                        foreach (Process p in Process.GetProcessesByName("OrleansHost", machine == "localhost" ? "." : machine))
                        {
                            var capture = p;
                            Task t = Task.Factory.StartNew(() =>
                            {
                                KillProcess(capture);
                            });
                            tasks.Add(t);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.WriteLine(SEV.ERROR, "Deployment.Silo", "Failed to stop silo processes on machine {0} due to exception {1}", machine, e.ToString());
                    }
                }
                Task.WaitAll(tasks.ToArray());
            }
            catch (Exception e)
            {
                Log.WriteLine(SEV.ERROR, "Deployment", "Failed to stop client or silo process due to exception {0}", e.ToString());
            }
        }
        #region File Copy stuff
        /// <summary>
        /// Makes a remote path by appending the machine name.
        /// </summary>
        /// <param name="machineName">name of the machine.</param>
        /// <param name="path">Corresponding local path for the machine.</param>
        /// <returns></returns>
        private static string MakeRemotePath(string machineName, string path)
        {
            if (path.StartsWith(@"\\")) // already a network path.
                return path;
            string remotePath = (@"\\" + machineName + @"\" + path)
                .Replace(":", "$");
            return remotePath;
        }
        
        /// <summary>
        /// Deletes the file/directory fromState remote machine.
        /// </summary>
        /// <param name="remotePath">remote path</param>
        public static void RemoveFiles(string remotePath)
        {
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
        /// Copies fromState file to file or fromState directory to directory.
        /// The remote path is specified as if it was a local path on the remote machine.
        /// Eg. C:\MyPath on MyMachine 
        /// </summary>
        /// <param name="from">path local to machine on which this method is running</param>
        /// <param name="to">destination remotepath</param>
        public static void CopyFiles(string from, string to)
        {
            if (from.Equals(to)) return;
            Log.WriteLine(SEV.INFO, "Deployment.Copy", "Copying from:{0} to:{1}", from, to);
            if (!File.Exists(from) && !Directory.Exists(from))
            {
                throw new FileNotFoundException("Cannot locate source for copy: " + from);
            }
            if ((File.GetAttributes(from) & FileAttributes.Directory) == FileAttributes.Directory)
            {
                if (!Directory.Exists(to))
                {
                    Log.WriteLine(SEV.INFO, "Deployment.Copy", "Creating directory:{0}", to);
                    Directory.CreateDirectory(to);
                }

                DirectoryInfo info = new DirectoryInfo(from);
                foreach (var file in info.GetFiles())
                {
                    CopyFiles(Path.Combine(from, file.Name), Path.Combine(to, file.Name));
                }
                foreach (var file in info.GetDirectories())
                {
                    CopyFiles(Path.Combine(from, file.Name), Path.Combine(to, file.Name));
                }
            }
            else
            {
                // just create the parent directory if it doesn't exist
                string parent = Path.GetDirectoryName(to);
                if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                List<Exception> exlist = new List<Exception>();
                bool copied = false;
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        File.Copy(from, to, true);
                        copied = true;
                        break;
                    }
                    catch (Exception ex)
                    {
                        exlist.Add(ex);
                    }
                }
                if (!copied) throw new AggregateException(exlist);
            }
        }
        
        /// <summary>
        /// Copy predefined dependencies to remote machine.
        /// </summary>
        /// <param name="destination"></param>
        private void CopyDependencies(string destination)
        {
            // Copy baby sitter
            string babysitterPath = //typeof(BabySitter.BabySitter).Assembly.Location;
                Path.Combine(Path.GetDirectoryName(typeof(DeploymentManager).Assembly.Location),"BabySitter.exe");
            CopyFiles(babysitterPath,Path.Combine(destination,Path.GetFileName(babysitterPath)));

            // First try if the local deployment path has it
            //var localLocation = Path.Combine(localDeploymentPath, "FSharp.Core.dll");
            //if (File.Exists(localLocation))
            //{
            //    CopyFiles(localLocation, Path.Combine(destination, "FSharp.Core.dll"));
            //}
            //else
            //{
            //    // TODO : fix the path
            //    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            //    {
            //        if (asm.Location.EndsWith("FSharp.Core.dll"))
            //        {
            //            CopyFiles(asm.Location, Path.Combine(destination, "FSharp.Core.dll"));
            //            break;
            //        }
            //    }
            //}
            try
            {
                RemoveFiles(Path.Combine(destination, Path.GetFileName(typeof(DeploymentManager).Assembly.Location)));
            }
            catch (Exception)
            {
            }
        }
        #endregion
        #region Silo Management
        /// <summary>
        /// Lauches the deployment
        /// </summary>
        public List<SiloHandle> LaunchServers(List<string> silos, ParserGrammar grammar = null, string waitState = null)
        {
            List<SiloHandle> processes = new List<SiloHandle>();
            
            // 2. generate configs
            string serverConfigFile = Path.Combine(tempDirForConfigGen, string.Format("Test_{0}_{1}.xml", UniqueRunId, uniqueTestId));
            configManager.GenerateClusterConfiguration(deployConfig, serverConfigFile);
            Assert.IsTrue(File.Exists(serverConfigFile));
            List<Task> serverLaunchTasks = new List<Task>();
            // 3. Deploy and Start
            foreach (string siloName in silos)
            {
                string siloNameCapture = siloName;
                Task t = Task.Factory.StartNew(() =>
                {
                    // Deploy to remote machine
                    string machine = deployConfig.GetAssignedSiloMachine(siloNameCapture);
                    // Deploy Silo
                    DeploySilo(machine, siloNameCapture);
                    // Start Silo
                    SiloHandle p = InitSilo(machine, siloNameCapture, serverConfigFile);
                    StartSilo(p);
                    Assert.IsNotNull(p);
                    lock (processes)
                    {
                        processes.Add(p);
                    }
                    // Wait
                    if (null != grammar && null != waitState)
                    {
                        QuickParser analyser = new QuickParser(grammar);
                        Assert.IsNotNull(p.LogFilePath);
                        analyser.BeginAnalysis(p.LogFilePath, () => HasExited(p.Process));
                        analyser.WaitForState(waitState);
                        p.ActiveListeners.Add(p.LogFilePath, analyser);
                    }
                    lock (logsToSave)
                    {
                        logsToSave.Add(p.LogFilePath);
                    }
                });
                serverLaunchTasks.Add(t);
            }
            // Wait untill all finished or one failed.
            TaskHelper.WaitUntilAllCompletedOrOneFailed(serverLaunchTasks);
            return processes;
        }
        

        /// <summary>
        /// Deploys orleans to remote machine.
        /// The silo will be deployed under "C:\TestResults\{UserName}-{MachineName}-{uniqueRunId}\{siloName}"
        /// </summary>
        /// <param name="machineName">Name of the machine to deploy to.</param>
        /// <param name="siloName">The name of the silo to be deployed on that machine</param>
        public void DeploySilo(string machineName, string siloName)
        {
            string destination = MakeRemotePath(machineName, Path.Combine(remoteDeploymentPath, siloName));
            if (!Directory.Exists(destination))
            {
                // Copy LocalSilo
                CopyFiles(localDeploymentPath, destination);
                    
                // Copy Additional Applications
                string applicationDir = Path.Combine(destination, "Applications");
                if (!Directory.Exists(applicationDir)) Directory.CreateDirectory(applicationDir);
                foreach (string app in deployConfig.Applications.Keys)
                {
                    string binaries = deployConfig.Applications[app];
                    string appPath = Path.Combine(applicationDir, app);
                    if (!Directory.Exists(appPath)) Directory.CreateDirectory(appPath);

                    Log.WriteLine(SEV.INFO, "Deployment.Silo", "Deploying Binaries for application {0} to Silo {1} on machine {2}. Copying from {3} to {4}", app, siloName, machineName, binaries, appPath);
                    //Log.WriteLine(SEV.INFO, "Deployment.Silo", "Copying from {0} on machine {1}", binaries, machineName);
                    //Log.WriteLine(SEV.INFO, "Deployment.Silo", "Copying to {0} on machine {1}", appPath, machineName);
                    CopyFiles(binaries, appPath);
                }
            }
        }

        public static void VerifyDeploymentMachine(string machineName)
        {
            if (machineName == Environment.MachineName)
                return;
            try
            {
                string deploymentRoot = MakeRemotePath(machineName, DeploymentRoot);
                if (!Directory.Exists(deploymentRoot))
                {
                    Directory.CreateDirectory(deploymentRoot);
                }
                File.AppendAllText(Path.Combine(deploymentRoot, "LastAccess.Log"),
                    string.Format("\nUser:{0} Machine:{1} Time:{2}", Environment.UserName, Environment.MachineName, DateTime.UtcNow));

                var mgmt = GetManagementScope(machineName);
            }
            catch (Exception e)
            {
                Log.WriteLine(SEV.INFO, "Deployment.VerifyDeploymentMachine", "Unable to access the machine {0}. Skipping it. \n\tException:{1}.\n", machineName, e);
                //Assert.Fail("Unable to access the machine {0} \n\tException:{1}", machineName, e);
            }
        }
        
        ///// <summary>
        ///// Starts the silo in the remote machine
        ///// </summary>
        ///// <param name="siloName">Name of the silo.</param>
        ///// <param name="deployConfig">Configs to pass to the silo.</param>
        ///// <returns>SiloHandle of the process</returns>
        //public ProcessHandle StartSilo(string siloName, string configFilePath)
        //{
        //    string machineName = deployConfig.GetAssignedSiloMachine(siloName);
        //    SiloHandle silo = InitSilo(machineName, siloName, configFilePath);
        //    StartSilo(silo);
        //    return silo;
        //}

        /// <summary>
        /// Starts the silo in the remote machine
        /// </summary>
        /// <param name="machineName">Name of the machine.</param>
        /// <param name="siloName">Name of the silo.</param>
        /// <param name="configFilePath">Configs to pass to the silo.</param>
        /// <returns>SiloHandle of the process</returns>
        public SiloHandle InitSilo(string machineName, string siloName, string configFilePath)
        {
            SiloHandle siloHandle = new SiloHandle();
            siloHandle.MachineName = machineName;
            siloHandle.Name = siloName;
            siloHandle.IsRunning = false;
            
            string remoteSiloDir = Path.Combine(remoteDeploymentPath, siloName);

            string processName = "OrleansHost.exe";
            string imagePath = Path.Combine(remoteSiloDir, processName); ;

            // This is a remote path as seen locally on remote machine. so starts as C:\
            string configRemoteDir = Path.Combine(remoteSiloDir, "GeneratedConfigs");
            string configRemotePath = Path.Combine(configRemoteDir, Path.GetFileName(configFilePath));

            // copy the files in remote case           
            CopyFiles(configFilePath, MakeRemotePath(machineName, configRemotePath));
            
            siloHandle.ConfigFilePath = MakeRemotePath(machineName, configRemotePath);
            siloHandle.ImagePath = imagePath;
            siloHandle.Parameters = new string[] {siloName, configRemotePath };
            return siloHandle;
        }
        /// <summary>
        /// Stops the silo
        /// </summary>
        /// <param name="siloHandle">Process handle for the silo to be stopped.</param>
        public void StopSilo(SiloHandle siloHandle)
        {
            KillProcess(siloHandle.Process);
            siloHandle.IsRunning = false;
        }

        public void RestartSilo(SiloHandle siloHandle)
        {
            StopSilo(siloHandle);
            StartSilo(siloHandle);
        }

        public void StartSilo(SiloHandle siloHandle)
        {
            string machineName = siloHandle.MachineName;
            string runDir = Path.GetDirectoryName(siloHandle.ImagePath);
            var dir = MakeRemotePath(machineName, runDir);
            var baseTime = GetBaseTime(dir);

            RunScript("DisableProxy.ps1", runDir, machineName);

            int processId = 0;
            try
            {
                processId = StartProcess(machineName, siloHandle.ImagePath, siloHandle.Parameters);

                siloHandle.Process = Process.GetProcessById(processId, machineName);
            }
            catch (Exception exc)
            {
                string errMsg = String.Format(
                    "Failed to GetProcessById: Could not find silo process Id {0} running on host {1} Silo = {2}",
                    processId, machineName, siloHandle);
                Log.WriteLine(SEV.ERROR, "Deployment.StartSilo {0}", errMsg);
                throw new AggregateException(errMsg, exc);
            }
          
            for (int i = 0; i < 50; i++)
            {
                var logfiles = FindLogFiles(siloHandle.Name, dir, baseTime);
                siloHandle.LogFilePath = logfiles.FirstOrDefault();
                if (null != siloHandle.LogFilePath) break;
                Assert.IsFalse(HasExited(siloHandle.Process), String.Format("OrleansHost {0} is not running, failed to initialize properly", siloHandle));
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }

            Assert.IsFalse(HasExited(siloHandle.Process), String.Format("OrleansHost {0} is not running, failed to initialize properly", siloHandle));
            Assert.IsNotNull(siloHandle.LogFilePath, "OrleansHost {0} taking too long to initialize. No output produced in 50 sec. Test Started at {1} now is {2} ", siloHandle, baseTime, DateTime.UtcNow);
            Assert.AreEqual(siloHandle.Process.MachineName, siloHandle.MachineName);
            siloHandle.IsRunning = true;
        }

        private void RunScript(string scriptName, string runDir, string machineName)
        {
            // See: http://stackoverflow.com/questions/4145232/path-to-powershell-exe-v-2-0
            const string powerShellExeDir = @"C:\Windows\System32\WindowsPowershell\v1.0\";

            string scriptPath = Path.Combine(runDir, scriptName);
            string[] args = new string[] {"-NoProfile", "-NonInteractive", "-File", scriptPath};
            string powershellExePath = Path.Combine(powerShellExeDir, "powershell.exe");

            Log.WriteLine(SEV.INFO, "Deployment.RunScript", "Running script {0} on machine {1}", scriptName, machineName);
            int processId = StartProcess(machineName, powershellExePath, args);
            if (processId <= 0)
            {
                string str = String.Format("Failed to start script {0} on machine {1} GetProcessById={2}, Path={3}.", scriptName, machineName, processId, scriptPath);
                Log.WriteLine(SEV.ERROR, "Deployment.RunScript", str);
                throw new Exception(str);
            }
        }

        #endregion
        #region Client Management
        /// <summary>
        /// Deploys client app to remote machine.
        /// </summary>
        /// <param name="machineName">Name of the machine to deploy to.</param>
        /// <param name="clientName">Name of the client program to be deployed.</param>
        public string DeployClient(string machineName, string clientName = "")
        {
            try
            {
                string deploymentRoot = MakeRemotePath(machineName, DeploymentRoot);
                if (!Directory.Exists(deploymentRoot))
                {
                    Directory.CreateDirectory(deploymentRoot);
                }
                string destination = Path.Combine(remoteDeploymentPath, clientName);
                string remoteDestination = MakeRemotePath(machineName, destination);
                if (!Directory.Exists(remoteDestination))
                {
                    Log.WriteLine(SEV.INFO, "Deployment.Client",
                        "Deploying Client {0} on machine {1}. Copying from {2} to {3}", clientName, machineName,
                        Path.GetDirectoryName(deployConfig.ClientAppPath), remoteDestination);
                    //Log.WriteLine(SEV.INFO, "Deployment.Client", "copying from {0} on machine {1}", Path.GetDirectoryName(deployConfig.ClientAppPath), machineName);
                    //Log.WriteLine(SEV.INFO, "Deployment.Client", "copying to {0} on machine {1}", remoteDestination, machineName);
                    CopyFiles(Path.GetDirectoryName(deployConfig.ClientAppPath), remoteDestination);

                    if (INPUT_GRAPH_FILE != null)
                    {
                        CopyFiles(INPUT_GRAPH_FILE, remoteDestination + "/" + Path.GetFileName(INPUT_GRAPH_FILE));
                    }

                    CopyDependencies(remoteDestination);
                }
                return remoteDestination;
            }
            catch (Exception e)
            {
                Log.WriteLine(SEV.ERROR, "Deployment", "Failed to DeployClient {0} on machine {1} due to exception {2}", clientName, machineName, e.ToString());
                throw;
            }
        }
        public ClientHandle LaunchClient(string clientName, string[] parameters, ParserGrammar grammar, string waitState, string[] gateways)
        {
            string configFile = Path.Combine(tempDirForConfigGen, string.Format("Test_{0}_{1}_{2}.xml", UniqueRunId, uniqueTestId, clientName));
            string logFile = string.Format("Test_{0}_{1}_{2}.log", UniqueRunId, uniqueTestId, clientName);
            configManager.GenerateClientConfiguration(clientName, deployConfig, configFile, gateways);
            Assert.IsTrue(File.Exists(configFile));
            // Deploy to remote machine
            string machine = deployConfig.GetAssignedClientMachine(clientName);
            Log.WriteLine(SEV.INFO, "Deployment.Client", "Launching client: on machine {0}", machine);
            Log.WriteLine(SEV.INFO, "Deployment.Client", "Client Name:{0} on machine {1}", clientName, machine);
            Log.WriteLine(SEV.INFO, "Deployment.Client", "Parameters:{0} on machine {1}", string.Join(" ", parameters), machine);
            //Log.WriteLine(SEV.INFO, machine, "Deployment.Silo", "Gateways:{0}", string.Join(" ", gateways));
            // Deploy client
            string clientDir = DeployClient(machine, clientName);
            DateTime baseTime = GetBaseTime(clientDir);
            // Init client machine
            string initScript = "DisableProxy.ps1";
            if (File.Exists(MakeRemotePath(machine, Path.Combine(clientDir, initScript))))
            {
                RunScript(initScript, clientDir, machine);
            }
            // Start client
            ClientHandle p = StartClient(machine, clientName, configFile, logFile, parameters ?? new string[] { }, true, baseTime);
            Assert.IsNotNull(p);
            // Wait
            if (null != grammar)
            {
                QuickParser analyser = new QuickParser(grammar);
                Assert.IsNotNull(p.LogFilePath);
                analyser.BeginAnalysis(p.LogFilePath, () => HasExited(p.Process));    
                analyser.WaitForState(waitState);
                p.ActiveListeners.Add(p.LogFilePath, analyser);
            }
            return p;
        }

        /// <summary>
        /// Starts client on a remote machine.
        /// </summary>
        public ClientHandle StartClient(string machineName, string clientName, string configFilePath, string logFilePath, string[] parameters, bool useBabySitter, DateTime baseTime)
        {
            ClientHandle client = new ClientHandle();
            client.MachineName = machineName;
            client.Name = clientName;
            string remoteClientDir = Path.Combine(remoteDeploymentPath, clientName);
            string configRemotePath = Path.Combine(Path.Combine(remoteClientDir, "GeneratedConfigs"), Path.GetFileName(configFilePath));
            string imagePath = deployConfig.ClientAppPath;
            string destination = Path.Combine(remoteDeploymentPath, clientName);

            imagePath = Path.Combine(destination, Path.GetFileName(deployConfig.ClientAppPath));
            CopyFiles(configFilePath, MakeRemotePath(machineName, configRemotePath));
            CopyFiles(configFilePath, MakeRemotePath(machineName, Path.Combine(destination, "ClientConfiguration.xml")));

            List<string> param = new List<string>();
            if (useBabySitter)
            {
                // add output file name
                param.Add(Quoted(Path.Combine(remoteClientDir, logFilePath)));
                //program to invoke
                param.Add(Quoted(imagePath));
                imagePath = Path.Combine(destination, Path.GetFileName(typeof(BabySitter).Assembly.Location));
            }
            param.AddRange(parameters);

            // For elasticity benchmark client, make sure we have a unique excel file name 
            // and we copy it to the log directory afterwards.
            string excelFilePath = null;
            for (int i = 0; i < param.Count; i++)
            {
                if (param[i] == "-excelName" && i < (param.Count - 1))
                {
                    string excelName = string.Format("{0}_{1}_{2}.xlsx", machineName, uniqueTestId, param[i + 1]);
                    param[i + 1] = excelName;
                    excelFilePath = Path.Combine(MakeRemotePath(machineName, remoteClientDir), excelName);
                }
            }

            int processId = StartProcess(machineName,
                imagePath,
                param.ToArray());

            List<string> clientLogs = null;
            string errFileName = null;
            try
            {
                client.LogFilePath = MakeRemotePath(machineName, Path.Combine(remoteClientDir, logFilePath));
                clientLogs = FindLogFiles("Client", Path.GetDirectoryName(client.LogFilePath), baseTime);
                errFileName = Path.Combine(MakeRemotePath(machineName, remoteClientDir), "BabysitterError.txt");
            }
            catch (Exception exc)
            {
                Log.WriteLine(SEV.ERROR, "StartClient", "Error locating log files for client {0} on node {1} -- {2}", client.Name, client.MachineName, exc);
            }

            lock (logsToSave)
            {
                if (client.LogFilePath != null)
                {
                    logsToSave.Add(client.LogFilePath);
                }
                if (clientLogs != null && clientLogs.Count > 0)
                {
                    logsToSave.AddRange(clientLogs);
                }
                if (errFileName != null)
                {
                    logsToSave.Add(errFileName);
                }
                if (excelFilePath != null)
                {
                    logsToSave.Add(excelFilePath);
                }
            }
            
            client.ConfigFilePath = MakeRemotePath(machineName, configRemotePath);
            lock (clientNamesToHandles)
            {
                clientNamesToHandles.Add(clientName, client);
            }

            if (useBabySitter)
            {
                if (errFileName != null && File.Exists(errFileName))
                {
                    string err = new StreamReader(errFileName).ReadToEnd();
                    throw new Exception(string.Format("Error launching client {0} on host {1}: {2}", client, machineName, err));
                }
            }

            try
            {
                client.Process = Process.GetProcessById(processId, machineName); //(Path.GetFileNameWithoutExtension(deployConfig.ClientAppPath), machineName)[0];
            }
            catch (Exception exc)
            {
                string errMsg = string.Format(
                    "Failed to GetProcessById: Could not find client process Id {0} running on host {1} Client log file = {2}",
                    processId, machineName, client.LogFilePath);
                Log.WriteLine(SEV.ERROR, "Deployment.StartClient {0}", errMsg);
                throw new AggregateException(errMsg, exc);
            }

            return client;
        }

        #endregion

        private List<ClientHandle> LaunchClients(int clientCount, string[] siloNames, ParserGrammar grammar, MetricCollector collector, int min, List<string> options)
        {
            int counter = 0;
            List<ClientHandle> clients = new List<ClientHandle>();
            List<Task> clientLaunchtasks = new List<Task>();
            for (int i = 0; i < clientCount; i++)
            {
                string clientName = "Client" + i;
                Task t = Task.Factory.StartNew(() =>
                {
                    List<string> parameters = new List<string>();
                    parameters.AddRange(options);
                    string[] gateways = new string[min];
                    // ReSharper disable AccessToModifiedClosure
                    for (int j = 0; j < min; j++)
                    {
                        gateways[j] = siloNames[counter];
                        counter = (++counter)%siloNames.Length;
                    }
                    // ReSharper restore AccessToModifiedClosure
                    string[] gwParams = new string[gateways.Length * 2];
                    for (int j = 0; j < gateways.Length; j++)
                    {
                        gwParams[j * 2] = "-gw";
                        gwParams[j * 2 + 1] = gateways[j];
                    }

                    foreach (string s in parameters) Log.WriteLine(SEV.INFO, "LaunchClients", "{0}", s);
                    var p = LaunchClient(clientName, parameters.ToArray(), grammar, "Printing", siloNames);
                    lock (clients)
                    {
                        clients.Add(p);
                        p.ActiveListeners[p.LogFilePath].MetricCollector = collector;
                        collector.AddSender(p.ActiveListeners[p.LogFilePath].FileName);
                    }
                });
                clientLaunchtasks.Add(t);
            }
            TaskHelper.WaitUntilAllCompletedOrOneFailed(clientLaunchtasks);
            Log.WriteLine(SEV.STATUS, "Deployment.Clients", "All clients are launched");
            return clients;
        }
        /// <summary>
        /// Starts the silo in the remote machine
        /// </summary>
        /// <param name="clientName">Name of the silo.</param>
        public void StopClient(string clientName)
        {
            lock (clientNamesToHandles)
            {
                ClientHandle client = clientNamesToHandles[clientName];
                KillProcess(client.Process);
                clientNamesToHandles.Remove(clientName);
            }
        }


        private static DateTime GetBaseTime(string dir)
        {
            string testName;

            if (Log.testConfig.TestContext != null)
                testName = Log.testConfig.TestContext.TestName;
            else
                testName = TestConfig.DefaulTestName;

            var fileName = Path.Combine(dir, testName);
            File.WriteAllText(fileName, DateTime.UtcNow.ToString());
            DateTime ret = File.GetCreationTimeUtc(fileName);
            return ret;
        }
        /// <summary>
        /// Find the log file based on patterned name
        /// </summary>
        /// <param name="prefix"></param>
        /// <param name="directory"></param>
        /// <param name="baseTime"></param>
        /// <param name="extension"></param>
        /// <returns></returns>
        public List<string> FindLogFiles(string prefix, string directory, DateTime baseTime ,string extension=".log")
        {
            List<string> ret = new List<string>();
            DirectoryInfo remoteDir = new DirectoryInfo(directory);
            foreach (var file in remoteDir.GetFiles(prefix + "*" + extension))
            {
                if (file.CreationTimeUtc >= baseTime) // only take file that was created after test was started.
                {
                    ret.Add(file.FullName);
                }
            }
            return ret;
        }
        /// <summary>
        /// Adds quotes to the string
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        static string Quoted(string s)
        {
            if (s.StartsWith("\"") && s.EndsWith("\""))
                return s;
            else
                return "\"" + s + "\"";
        }
        #region process management

        /// <summary>
        /// Starts a process on remote machine.
        /// </summary>
        /// <param name="machineName">Name of the machine.</param>
        /// <param name="processPath">Local path to the excutible.</param>
        /// <param name="parameters">List of parameters to pass to the process.</param>
        /// <returns></returns>
        public static int StartProcess(string machineName, string processPath, string[] parameters)
        {
            int processId;

            StringBuilder args = new StringBuilder();
            foreach (string s in parameters)
            {
                args.AppendFormat(" {0} ", Quoted(s));
            }
            string cmdLineArgs = args.ToString();

            if (machineName == ".") machineName = "localhost";

            if (machineName == "localhost")
            {
                processId = StartLocalProcess(cmdLineArgs, processPath);
            }
            else
            {
                processId = StartRemoteProcess(cmdLineArgs, processPath, machineName);
            }
            Log.WriteLine(SEV.INFO, "Deployment.StartProcess", "Done. Process id = {0}, machineName = {1}", processId, machineName);
            return processId;
        }

        private static int StartLocalProcess(string cmdLineArgs, string processPath)
        {
            Process retValue;
            int processId;
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = processPath;
            startInfo.CreateNoWindow = true;
            startInfo.WorkingDirectory = Path.GetDirectoryName(processPath);
            startInfo.UseShellExecute = false;
            startInfo.Arguments = cmdLineArgs;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            retValue = Process.Start(startInfo);
            processId = retValue.Id;
            if (retValue.HasExited && retValue.ExitCode != 0)
            {
                string stdout = retValue.StandardOutput.ReadToEnd();
                string stderror = retValue.StandardError.ReadToEnd();
                throw new Exception(String.Format("Error while invoking process \n\n{0}\n\n{1}", stdout, stderror));
            }
            return processId;
        }

        private static int StartRemoteProcess(string cmdLineArgs, string processPath, string machineName)
        {
            int processId;
            
            string commandline = string.Format("{0} {1}", processPath, cmdLineArgs);

            ManagementScope scope = GetManagementScope(machineName);

            ObjectGetOptions objectGetOptions = new ObjectGetOptions();
            ManagementPath managementPath = new ManagementPath("Win32_Process");
            ManagementClass processClass = new ManagementClass(scope, managementPath, objectGetOptions);
            ManagementBaseObject inParams = processClass.GetMethodParameters("Create");
            inParams["CommandLine"] = commandline;
            inParams["CurrentDirectory"] = Path.GetDirectoryName(processPath);
            Log.WriteLine(SEV.INFO, "Deployment.StartProcess", "Creating remote process: on machine {0}", machineName);
            Log.WriteLine(SEV.INFO, "Deployment.StartProcess", "Machine:{0}", machineName);
            Log.WriteLine(SEV.INFO, "Deployment.StartProcess", "CommandLine:{0} on machine {1}", inParams["CommandLine"], machineName);
            Log.WriteLine(SEV.INFO, "Deployment.StartProcess", "Working directory:{0} on machine {1}", inParams["CurrentDirectory"], machineName);
            ManagementBaseObject outParams = processClass.InvokeMethod("Create", inParams, null);
            Thread.Sleep(TimeSpan.FromSeconds(15));
            if (outParams != null)
            {
                object data = outParams["processId"];
                if (data != null)
                {
                    processId = int.Parse(data.ToString());
                }
                else
                {
                    string errMsg = string.Format("Remote process {0} did not return process Id on machine {1}", processPath, machineName);
                    Log.WriteLine(SEV.ERROR, "Deployment.StartProcess", errMsg);
                    throw new Exception(errMsg);
                }
            }
            else
            {
                string errMsg = string.Format("Remote process {0} did not start on machine {1}", processPath, machineName);
                Log.WriteLine(SEV.ERROR, "Deployment.StartProcess", errMsg);
                throw new Exception(errMsg);
            }
            return processId;
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
            catch (Exception)
            {
                if (!(process.MachineName == "." || process.MachineName == "localhost" || process.MachineName == Environment.MachineName))
                {
                    try
                    {
                        // connect
                        ManagementScope scope = GetManagementScope(process.MachineName);

                        // find the process
                        ObjectQuery query = new System.Management.ObjectQuery(string.Format("Select * from Win32_Process where ProcessId='{0}'", process.Id));
                        ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher(scope, query);
                        ManagementObjectCollection results = searcher.Get();

                        // Terminate the process
                        foreach (System.Management.ManagementObject remoteprocess in results)
                        {
                            // id of process
                            if (int.Parse(remoteprocess["ProcessId"].ToString()) == process.Id)
                            {
                                object[] obj = { 0 };
                                remoteprocess.InvokeMethod("Terminate", obj);
                            }
                        }
                    }
                    catch (Exception )
                    {
                    }
                }
            }
        }

        /// <summary>
        /// Kills the process.
        /// In case of remote processes may use WMI to terminate the process.
        /// </summary>
        /// <param name="process">Process to kill.</param>
        public static bool HasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception)
            {
                if (!(process.MachineName == "." || process.MachineName == "localhost" || process.MachineName == Environment.MachineName))
                {
                    try
                    {
                        // connect
                        ManagementScope scope = GetManagementScope(process.MachineName);

                        // find the process
                        ObjectQuery query = new System.Management.ObjectQuery(string.Format("Select * from Win32_Process where ProcessId='{0}'", process.Id));
                        ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher(scope, query);
                        ManagementObjectCollection results = searcher.Get();

                        // Terminate the process
                        foreach (System.Management.ManagementObject remoteprocess in results)
                        {
                            lock (wmiFailures)
                            {
                                wmiFailures[process.MachineName] = 0;
                            }
                            return false;
                        }
                        
                    }
                    catch
                    {
                        // if there is an exception then assume that process might be still running.
                        // But don't allow more than 10 failures .
                        lock (wmiFailures) // this is global and we have over 40 threads!
                        {
                            if (wmiFailures[process.MachineName] >= 10)
                            {
                                return true;
                            }
                            else
                            {
                                wmiFailures[process.MachineName]++;
                                return false;
                            }
                        }
                    }
                }
                return true;
            }
        }

        #endregion

        ///// <summary>
        ///// Runs a powershell script
        ///// </summary>
        ///// <param name="scriptPath"></param>
        ///// <param name="options"></param>
        //public static void RunScript(string scriptPath, params string[] options)
        //{
        //    Command command = new Command(scriptPath + " " + string.Join(" ", options), true);

        //    RunspaceConfiguration runspaceConfiguration = RunspaceConfiguration.Create();

        //    Runspace runspace = RunspaceFactory.CreateRunspace(runspaceConfiguration);
        //    runspace.Open();

        //    RunspaceInvoke scriptInvoker = new RunspaceInvoke(runspace);

        //    using (Pipeline pipeline = runspace.CreatePipeline())
        //    {
        //        pipeline.Commands.Add(command);

        //        try
        //        {
        //            var results = pipeline.Invoke();
        //        }
        //        catch (Exception e)
        //        {
        //            Log.WriteLine("Exception" + e.ToString());
        //        }
        //    }
        //}

        bool testFinished = false;
        public void TestFinished()
        {
            testFinished = true;
            foreach (var client in GetActiveClientLogParsers())
            {
                client.EndAnalysis();
            }
        }
        public List<QuickParser> GetActiveClientLogParsers()
        {
            List<QuickParser> logs = new List<QuickParser>();

            foreach (ProcessHandle p in Clients)
            {
                logs.Add(p.ActiveListeners[p.LogFilePath]);
            }
            return logs;
        }
        public List<QuickParser> GetActiveServerLogParsers()
        {
            List<QuickParser> logs = new List<QuickParser>();

            foreach (ProcessHandle p in Clients)
            {
                logs.Add(p.ActiveListeners[p.LogFilePath]);
            }
            return logs;
        }

        public void StartTestEnvironment(ClientOptions clientOptions, ParserGrammar svrGrmr, ParserGrammar grammar, MetricCollector collector)
        {
            List<string> options = clientOptions.AsParameters();

            Log.WriteLine(SEV.INFO, "Config.Init", "DeploymentId = " + deployConfig.DeploymentId + ", ServiceId = " + deployConfig.ServiceId);
            
            //--- STEP 1: Launch Silos ---
            Log.WriteLine(SEV.INFO, "Deployment.Silo","Launching servers..");
            
            // assign machines
            List<string> silos = deployConfig.PreAssignSiloMachines(clientOptions.ServerCount);
            Assert.AreEqual(silos.Count, clientOptions.ServerCount);

            // Launch servers
            Silos = LaunchServers(silos, svrGrmr);
            var siloNames = (from siloName in silos select deployConfig.GetAssignedSiloMachine(siloName)).ToArray();

            TimeSpan waitTime = QuickParser.WAIT_FOR_SILOS_TO_STABILIZE;
            if (QuickParser.DEBUG_ONLY_NO_WAITING)
            {
                waitTime = TimeSpan.FromSeconds(10);
            }
            // Let the Silos become stable
            Thread.Sleep(waitTime);
            DumpAzureTablePartition();
            //--- STEP 2: Setup metric collection options ---
            SetRuntimeVariables(clientOptions, collector);
            
            //--- STEP 3: launch clients ---
            int min = (clientOptions.UseAzureSiloTable) ? 0 : (clientOptions.ServersPerClient < siloNames.Length) ? clientOptions.ServersPerClient : siloNames.Length;

            Clients = LaunchClients(clientOptions.ClientCount, siloNames, grammar, collector, min, options);
            Logs = GetActiveClientLogParsers();
            //List<QuickParser> servers = GetActiveServerLogParsers(Silos);

            //--- STEP 4: Wait until every client is stable ---
            QuickParser.WaitForStateAll(Logs, "Stable");
            Log.WriteLine(SEV.STATUS, "Deployment.Client","All clients are in state \"Stable\" now. Starting to analyze data.");
        }

        public void SetRuntimeVariables(ClientOptions clientOptions, MetricCollector collector)
        {
            Log.TestResults.LogDirectory = LogoutputPath;
            Log.TestResults.GlobalResultsFileName = Path.Combine(LogoutputPath, collector.Name + ".global.csv");
            Log.AttachmentFiles.Add(Log.TestResults.GlobalResultsFileName);
            collector.AddVariable("ServerCount", clientOptions.ServerCount);
            collector.AddVariable("ClientCount", clientOptions.ClientCount);
            double scaleFactor = Math.Min(
                clientOptions.ClientCount * clientOptions.ServersPerClient * 1000,
                clientOptions.ServerCount * 2500);
            collector.AddVariable("ScaleFactor", scaleFactor);
            collector.AddVariable("ScaleFactorPerClient", scaleFactor / clientOptions.ClientCount);
        }

        public void WaitUntilTestFinish()
        {
            while (!testFinished)
            {
                Thread.Sleep(TimeSpan.FromSeconds(10));
            }
        }

        public void BabysitSilos()
        {
            List<string> startupErrorFiles = new List<string>();
            List<string> failedSilos = new List<string>();
                
            while (!testFinished)
            {
                foreach (SiloHandle silo in Silos)
                {
                    if (testFinished) return;
                    if (!silo.IsRunning) continue; // Keep looping if silo not yet started
                    if (DeploymentManager.HasExited(silo.Process))
                    {
                        failedSilos.Add(silo.Name);
                    }
                    string startupError = null;
                    try
                    {
                        startupError = FindLogFiles(silo.Name + "-StartupError", Path.GetDirectoryName(silo.LogFilePath), BaseTime, ".txt").FirstOrDefault();
                    }
                    catch (Exception exc)
                    {
                        Log.WriteLine(SEV.ERROR, "BabysitSilos", "Error locating startup error file for silo {0} on node {1} -- {2}", silo.Name, silo.MachineName, exc);
                    }
                    if(null != startupError)
                    {
                        startupErrorFiles.Add(startupError);
                    }
                }
                if (startupErrorFiles.Count > 0)
                {
                    lock (logsToSave)
                    {
                        logsToSave.AddRange(startupErrorFiles);
                    }
                }
                if(failedSilos.Count > 0 || startupErrorFiles.Count > 0)
                {
                    failedSilos.Sort();
                    throw new Exception(String.Format("{0} silos exited abnormally. Failed silos are: {1}", failedSilos.Count, Utils.IEnumerableToString(failedSilos)));
                }
                Thread.Sleep(TimeSpan.FromMinutes(1));
            }
        }

        public void BabysitClients()
        {
            // NOTE : output of a client is processed on timer thread, 
            // so there might be delay between we detect that process has ended and actual log processing is complete.
            // we should wait for 2 minutes giving enough time to drain the log processing queue.
            // Instead of waiting for each client that exited, it is efficient to wait for all in the end.
            List<ClientHandle> runningClients = new List<ClientHandle>();
            runningClients.AddRange(Clients);
            while (!testFinished)
            {        // check all clients

                List<ClientHandle> finishedClients = new List<ClientHandle>();
                foreach (ClientHandle client in runningClients)
                {
                    if (DeploymentManager.HasExited(client.Process))
                    {
                        finishedClients.Add(client);
                    }
                }
                if (finishedClients.Count > 0)
                {
                    // Wait 
                    Thread.Sleep(TimeSpan.FromMinutes(1));

                    // Now throw if client process exited without completing properly.
                    foreach (ClientHandle client in finishedClients)
                    {
                        if (QuickParser.DEBUG_ONLY_NO_WAITING)
                        {
                            runningClients.Remove(client);
                        }else
                        {
                            if (client.ActiveListeners[client.LogFilePath].VisitedStates.Contains("Finished"))
                            {
                                runningClients.Remove(client);
                            }
                            else
                            {
                                throw new Exception(string.Format("Client exited abnormally {0}", client));
                            }
                        }
                    }
                }
                Thread.Sleep(TimeSpan.FromMinutes(1));
            }
        }

        public void DumpAzureTablePartition()
        {
            //try
            //{
            //    // Retrieve storage account from connection-string
            //    string connectionString = configManager.GetValueFromTemplate("/oc:OrleansConfiguration/oc:Globals/oc:Azure", "DataConnectionString");
            //    CloudStorageAccount storageAccount = CloudStorageAccount.Parse(connectionString);

            //    // Create the table client
            //    CloudTableClient tableClient = storageAccount.CreateCloudTableClient();

            //    // Get the data service context
            //    TableServiceContext serviceContext = tableClient.GetDataServiceContext();

            //    // Specify a partition query, using DeploymentID as the partition key
            //    CloudTableQuery<SiloInstanceTableEntry> partitionQuery =
            //        (from e in serviceContext.CreateQuery<SiloInstanceTableEntry>("OrleansSiloInstances")
            //         where e.PartitionKey == deployConfig.DeploymentId
            //         select e).AsTableServiceQuery<SiloInstanceTableEntry>();

            //    // Loop through the results, displaying information about the entity 
            //    //StringBuilder strBuilder = new StringBuilder();
            //    //strBuilder.Append(String.Format("Deployment {0}. Silos: ", deployConfig.DeploymentId));
            //    string heading = String.Format("Deployment {0}. Silos: ", deployConfig.DeploymentId);
            //    Log.WriteLine(SEV.STATUS, "AZURE.OrleansSiloInstances", "{0}", heading);
            //    SiloInstanceTableEntry[] entries = partitionQuery.ToArray();
            //    Array.Sort(entries,
            //        (e1, e2) =>
            //        {
            //            if (e1 == null) return (e2 == null) ? 0 : -1;
            //            if (e2 == null) return (e1 == null) ? 0 : 1;
            //            if (e1.InstanceName == null) return (e2.InstanceName == null) ? 0 : -1;
            //            if (e2.InstanceName == null) return (e1.InstanceName == null) ? 0 : 1;
            //            return e1.InstanceName.CompareTo(e2.InstanceName);
            //        });
            //    foreach (SiloInstanceTableEntry entry in entries)
            //    {
            //        string str = String.Format("[IP {0}:{1}:{2}, {3}, Instance={4}, Status={5}]", entry.Address, entry.Port, entry.Generation, 
            //            entry.HostName, entry.InstanceName, entry.Status);
            //        Log.WriteLine(SEV.STATUS, "AZURE.OrleansSiloInstances", "{0}", str);
            //        //strBuilder.Append(str).Append(Environment.NewLine);
            //        //Log.WriteLine(SEV.STATUS, Environment.MachineName, "AZURE.OrleansSiloInstances", "{0}, {1}", entry.PartitionKey, entry.ToString());
            //    }
            //    //Log.WriteLine(SEV.STATUS, "AZURE.OrleansSiloInstances", "{0}", str.ToString());
            //}
            //catch(Exception ex)
            //{
            //    Log.WriteLine(SEV.STATUS,  "AZURE.OrleansSiloInstances", "Error reading the table. {0}",ex);
            //}
        }
    }
}
