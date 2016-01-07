using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UnitTests
{
    [TestClass]
    public class DeployTest
    {
        static string factoryCacheFolder;
        static string orleansConfigFolder;
        string oldExecutionPolicy;
        static string preTestFolder; 


        #region Test Initialization
        [ClassInitialize]
        public static void ClassInit(TestContext testContext)
        {
            string localAppDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string orleansDataFolder = Path.Combine(localAppDataFolder, "OrleansData");
            factoryCacheFolder = Path.Combine(orleansDataFolder, "FactoryCache");
            string testResultsFolder = testContext.TestResultsDirectory;
            // Read the Orleans dir out of the configuration file.
            orleansConfigFolder = Path.Combine(Environment.CurrentDirectory, "Configuration");
        }

        [TestInitialize]
        public void InitializeTest()
        {
            //if (!RunningAsAdmin())
            //{
            //    Assert.Inconclusive("Cannot Run Test: The Orleans deployment PowerShell scripts require Visual Studio to be run with elevated privileges.");
            //}

            // If the test does have elevated permission, make sure that it can run PowerShell scripts.
            Collection<PSObject> results = RunPowerShellCommand("Get-ExecutionPolicy");
            oldExecutionPolicy = results[0].ToString();
            if (string.Compare(oldExecutionPolicy, "restricted", true) == 0)
            {
                if (RunningAsAdmin())
                {
                    RunPowerShellCommand("Set-ExecutionPolicy RemoteSigned -Force -Scope LocalMachine");
                }
                else
                {
                    Assert.Inconclusive("Cannot Run Test: The Orleans deployment PowerShell scripts require ExecutionPolicy be set to RemoteSigned.");
                }
            }

            preTestFolder = Environment.CurrentDirectory;
        }

        [TestCleanup]
        public void CleanupTest()
        {
            Environment.CurrentDirectory = preTestFolder;

            if (RunningAsAdmin())
            {
                RunPowerShellCommand("Set-ExecutionPolicy", oldExecutionPolicy, "-Force", "-Scope LocalMachine");
            }
        }

        #endregion Test Initialization

        #region Tests: CleanOrleansSilos.ps1

        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Management")]
        public void CleanOrleans_RequestHelp_UseageDisplayed()
        {
            CheckForUsageText(".\\CleanOrleansSilos.ps1");
        }

        /// <summary>
        /// Tests that the CleanOrleansSilos.ps1 script deletes all the files in orleansFolder and factoryCacheFolder.
        /// </summary>
        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Failures")]
        public void CleanOrleans_OrleansAndCacheDirsExist_OrleansFilesDeleted()
        {
            string targetFolder = PrepareTestFolderStructure("CleanOrleans");

            // Use an absolute path to avoid ambiguity.
            string configFileFullPath = Path.Combine(targetFolder, "Configuration\\TestCleanOrleans\\Clean-RelativePaths.xml");

            if (!Directory.Exists(factoryCacheFolder))
            {
                Directory.CreateDirectory(factoryCacheFolder);
                // Maybe copy some files here to confirm that the delete actually does something.
            }

            // Add single quotes to the string so that the PowerShell can use paths with spaces.
            string safePathString = string.Format("'{0}'", configFileFullPath);
            Collection<PSObject> results = RunPowerShellCommand(".\\CleanOrleansSilos.ps1", safePathString);

            string[] orleansFilesAfterScript = Directory.GetFiles(targetFolder, "*.*", SearchOption.AllDirectories);
            int orleansFileCountAfterScript = orleansFilesAfterScript.Count();

            List<string> nonPdbFiles = new List<string>(orleansFilesAfterScript.Count());
            foreach (string fileName in orleansFilesAfterScript)
            {
                if (!fileName.EndsWith(".pdb"))
                {
                    nonPdbFiles.Add(fileName);
                }

            }

            Assert.AreEqual(0, nonPdbFiles.Count, "{0} files (non-.pdb) were not removed from the target directory ({1}).", nonPdbFiles.Count, targetFolder);

            string[] cacheFilesAfterScript = Directory.GetFiles(factoryCacheFolder, "*.*", SearchOption.AllDirectories);
            int cacheFileCountAfterScript = cacheFilesAfterScript.Count();

            Assert.AreEqual(0, cacheFileCountAfterScript, "{0} files were not deleted from the cache folder ({1}).", cacheFileCountAfterScript, factoryCacheFolder);
        }

        /// <summary>
        /// Test to make sure that the CleanOrleansSilos.ps1 script gracefully handles the condition where 
        /// the folders are already missing.
        /// </summary>
        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Failures")]
        public void CleanOrleans_OrleansAndCacheDirsDoNotExist_NoErrorReported()
        {
            // Create an empty folder structure.
            string targetFolder = BuildFolderTree("CleanOrleansEmpty");

            Collection<PSObject> results = RunPowerShellCommand(".\\CleanOrleansSilos.ps1", "Configuration\\TestCleanEmptyOrleans\\Clean-EmptyRelativePaths.xml");

            // Check the results for an error or Exception.
            CheckResultsForErrors(results);
        }

        #endregion Test: CleanOrleansSilos.ps1

        #region Tests: IsProcessRunning.ps1

        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Management")]
        public void IsProcessRunning_RequestHelp_UseageDisplayed()
        {
            CheckForUsageText(".\\StopOrleans.ps1");
        }

        /// <summary>
        /// This test uses a non-Orleans process to test the script that determines if Orleans is running.
        /// </summary>
        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Management")]
        public void IsProcessRunning_ProcessIsRunning_ReportTrue()
        {
            ProcessStartInfo calcStartInfo = new ProcessStartInfo("calc.exe");
            calcStartInfo.UseShellExecute = false;
            Process calcProcess = Process.Start(calcStartInfo);
            // Give the process time to start up.
            System.Threading.Thread.Sleep(500);
            string processName = string.Format("\"{0}\"", calcProcess.ProcessName);
            Collection<PSObject> results = RunPowerShellCommand(".\\IsProcessRunning.ps1", calcProcess.ProcessName, "localHost");
            // Run the script; result should be "True".
            Assert.IsNotNull(results, "No results returned (results = null).");
            Assert.IsTrue(results.Count > 0, "Process not found on target machine (results.Count = 0).");
            Assert.IsTrue(results[0].ToString().Contains(calcProcess.Id.ToString()), "The .\\IsProcessRunning script did not detect the running process.");
            // Clean up after the test.
            calcProcess.Kill();
        }

        /// <summary>
        /// This test uses a non-Orleans process to test the script that determines if Orleans is running.
        /// </summary>
        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Management")]
        public void IsProcessRunning_ProcessNotRunning_ReportFalse()
        {
            // Shut down any instances of calc.
            Process[] calcProcess = Process.GetProcessesByName("calc");
            foreach (Process orleansHostProcess in calcProcess)
            {
                orleansHostProcess.Kill();
            }

            // Run the script; the result should be "False".
            Collection<PSObject> results = RunPowerShellCommand(".\\IsProcessRunning.ps1", "calc", "localHost");

            if (results.Count != 0)
            {
                // Check the results for an error or exception.
                CheckResultsForErrors(results);

                StringBuilder resultsBuilder = new StringBuilder();
                foreach (PSObject result in results)
                {
                    resultsBuilder.AppendLine(result.ToString());
                }
                Assert.Fail("IsProcessRunning.ps1 script returned information when it should have return nothing: {0}", resultsBuilder);
            }
        }

        #endregion IsProcessRunning.ps1

        #region Tests: StartOrleans.ps1
        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Management")]
        public void StartOrleans_RequestHelp_UseageDisplayed()
        {
            CheckForUsageText(".\\StartOrleans.ps1");
        }

        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Failures")]
        public void StartOrleans_OrleansNotStarted_ProcessStarted()
        {
            // Stop any currently running Orleans processes.
            StopProcesses("OrleansHost");

            string targetFolder = PrepareTestFolderStructure("StartOrleansTest");

            // Run the script from the target directory - the test clean up will restore it to its previous value.
            Environment.CurrentDirectory = targetFolder;

            Collection<PSObject> results = RunPowerShellCommand(".\\StartOrleans.ps1", "Silo0");
            System.Threading.Thread.Sleep(9000);
            Process[] orleansHostTestProcess = Process.GetProcessesByName("OrleansHost");
            // Check the results for an error or exception.
            CheckResultsForErrors(results);
            Assert.IsTrue(orleansHostTestProcess.Count() > 0, "OrleansHost not started by .\\StartOrleans.ps1");

        }

        #endregion

        #region Tests: StopOrleans.ps1
        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Management")]
        public void StopOrleans_RequestHelp_UseageDisplayed()
        {
            CheckForUsageText(".\\StopOrleans.ps1");
        }

        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Management")]
        public void StopOrleans_OrleansNotRunning_NotRunningMessage()
        {
            // Shut down any instances of OrleansHost.
            string processName = "OrleansHost";
            StopProcesses(processName);

            // Test script when Orleans Host is not running.
            Collection<PSObject> results = RunPowerShellCommand(".\\StopOrleans.ps1");

            // Check to see if Orleans Host is running.
            Process[] orleansHostTestProcess = Process.GetProcessesByName("OrleansHost");

            CheckResultsForErrors(results);

            Assert.IsTrue(orleansHostTestProcess.Count() < 1, "OrleansHost is still running.");

            // Check to see if the expected message or an error was returned.
            KeyValuePair<string, string> notStartedSearchPair = new KeyValuePair<string, string>("\tOrleansHost is not running on deployment machine(s)", "The script did not report that the OrleansHost was not started.");
            CheckResultsForStrings(results, true, notStartedSearchPair);
        }

        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Management")]
        public void StopOrleans_OrleansIsRunning_OrleansStopped()
        {
            Process[] orleansHostTestProcess = Process.GetProcessesByName("OrleansHost");

            // If Orleans is already running, there is really no need to start it up just to shut it down.
            if (orleansHostTestProcess.Count() < 1)
            {
                string targetFolder = PrepareTestFolderStructure("StopOrleansTest");
                // Get Orleans running.
                string orleansHostPath = Path.Combine(targetFolder, "OrleansHost.exe");
                ProcessStartInfo orleansStartInfo = new ProcessStartInfo(orleansHostPath);
                orleansStartInfo.UseShellExecute = false;
                Process orleansProcess = Process.Start(orleansStartInfo);
                // Pause to let Orleans get started up.
                System.Threading.Thread.Sleep(9000);
                orleansHostTestProcess = Process.GetProcessesByName("OrleansHost");
                if (orleansHostTestProcess.Count() < 1)
                {
                    Assert.Inconclusive("Cannot Run Test: Could not start an OrleansHost necessary to test the StopOrleans.ps1 script.");
                }
            }

            // Test script when Orleans Host is not running.
            Collection<PSObject> results = RunPowerShellCommand(".\\StopOrleans.ps1");
            System.Threading.Thread.Sleep(5000);

            // Check to see if Orleans Host is still running.
            orleansHostTestProcess = Process.GetProcessesByName("OrleansHost");
            Assert.IsTrue(orleansHostTestProcess.Count() < 1, "OrleansHost is still running.");

            // Check the results for an error or exception.
            CheckResultsForErrors(results);
        }
        #endregion StopOrleans.ps1

        #region Tests: DeployOrleansSilos.ps1

        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Management")]
        public void DeployOrleans_RequestHelp_UseageDisplayed()
        {
            CheckForUsageText(".\\DeployOrleansSilos.ps1");
        }

        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Management")]
        public void DeployOrleans_DefaultDeploy_OrleansDeployed()
        {
            TestOrleansDeployment("DefaultDeploy", "Deployment.xml");
        }

        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Management")]
        public void DeployOrleans_RelativePathDeploy_OrleansDeployed()
        {
            TestOrleansDeployment("RelativeDeploy", "'.\\Configuration\\TestRelativePaths\\Deploy-RelativePaths.xml'");
        }

        [TestCategory("Deployment Scripts"), TestMethod, TestCategory("Management")]
        public void DeployOrleans_AbsolutePathDeploy_OrleansDeployed()
        {
            // TODO: Modify the OrleansConfiguration file to use the test-specific directory for this run.
            TestOrleansDeployment("RelativeDeploy", "'.\\Configuration\\TestAbsolutePaths\\Deploy-AbsolutePaths.xml'");
        }

        #endregion Tests: DeployOrleansSilos.ps1

        #region Support Methods
        /// <summary>
        /// Builds an empty folder tree for a test.  May be used to test scripts against empty deployment sources.
        /// </summary>
        /// <param name="testRootFolder">A name for the test.  Don't include spaces, or any Filename\Path characters.</param>
        /// <returns>Returns a string containing the full path to the root folder for the tree.</returns>
        private static string BuildFolderTree(string testRootFolder)
        {
            // Create a test root folder.
            string relativePath = Path.Combine("..", testRootFolder);
            DirectoryInfo testFolderInfo = Directory.CreateDirectory(relativePath);

            // Create a deploy directory for Orleans where it will be installed from for this test.
            string deployFolder = Path.Combine(testFolderInfo.FullName, "Deploy");
            DirectoryInfo deployFolderInfo = Directory.CreateDirectory(deployFolder);

            return deployFolderInfo.FullName;
        }

        /// <summary>
        /// Checks to see if the script returns the usage text when help is requested.
        /// </summary>
        /// <param name="scriptName">
        ///     The name of the script to test with extension.  Prefix with .\\ so PowerShell can tell the 
        ///     difference from a script and a command
        /// </param>
        private void CheckForUsageText(string scriptName)
        {
            // Test "/?"
            Collection<PSObject> results = RunPowerShellCommand(scriptName, "/?");
            string usageText = results[1].ToString();
            Assert.IsTrue(usageText.StartsWith("\tUsage:"), "Usage text not displayed when first parameter is /?");

            // Test "/help"
            results = RunPowerShellCommand(scriptName, "/help");
            usageText = results[1].ToString();
            Assert.IsTrue(usageText.StartsWith("\tUsage:"), "Usage text not displayed when first parameter is /help");

            // Test "help"
            results = RunPowerShellCommand(scriptName, "help");
            usageText = results[1].ToString();
            Assert.IsTrue(usageText.StartsWith("\tUsage:"), "Usage text not displayed when first parameter is help");


            // TODO: Enable when code is added to the script to detect the dash character differently.
            //// Test "-?"
            //results = InvokePowerShellScript(scriptName, "-?");
            //useageText = results[1].ToString();
            //Assert.IsTrue(usageText.StartsWith("\tUsage:"), "Usage text not displayed when first parameter is -?");

            //// Test "-help"
            //results = InvokePowerShellScript(scriptName, "-help");
            //useageText = results[1].ToString();
            //Assert.IsTrue(usageText.StartsWith("\tUsage:"), "Usage text not displayed when first parameter is -help");
        }

        /// <summary>
        /// More specialized version of CheckResultsForStrings that looks for "error" and "exception".
        /// </summary>
        /// <param name="results">The results collection to search.</param>
        private static void CheckResultsForErrors(Collection<PSObject> results)
        {
            // Check the results for an error or exception.
            KeyValuePair<string, string> errorSearchPair = new KeyValuePair<string, string>("error", "An error was reported by the script: \"{0}\"");
            KeyValuePair<string, string> exceptionSearchPair = new KeyValuePair<string, string>("exception", "An exception was reported by the script: \"{0}\"");
            CheckResultsForStrings(results, false, errorSearchPair, exceptionSearchPair);
        }


        /// <summary>
        /// Searches the collection of PSObjects for the Key(s) provided and fails the assertion if the 
        /// Boolean result does not match the value of the "assertKeysAreFound" flag.  
        /// (i.e. it fails if the Key is found and assertKeysAreFound is false, or if the Key is not found 
        /// and assertKeysAreFound is true.  If the assertion fails, the reported message is taken from the 
        /// Value of the pair with the Key that failed.
        /// </summary>
        /// <param name="results">The collection of PowerShell results.</param>
        /// <param name="assertKeysAreFound">
        ///     Flag to indicate if the search is for the presence or absence of the keys.
        ///     If true, expecting to find the keys in the results, and if false expecting the keys to be 
        ///     absent from the results.
        /// </param>
        /// <param name="searchPairs">
        ///     KeyValuePair objects that contain a Key to search for in the result and a Value to 
        ///     report if the assertion fails. If string in the Value contains the index-zero format parameter,
        ///     ( {0} ), it will be filled with the string returned from the PowerShell results.
        /// </param>
        private static void CheckResultsForStrings(Collection<PSObject> results, bool assertKeysAreFound, params KeyValuePair<string, string>[] searchPairs)
        {
            foreach (PSObject result in results)
            {
                string resultString = result.ToString();
                foreach (KeyValuePair<string, string> searchPair in searchPairs)
                {
                    bool found = resultString.IndexOf(searchPair.Key, StringComparison.OrdinalIgnoreCase) != -1;
                    Assert.IsTrue(found == assertKeysAreFound, searchPair.Value, resultString);
                }
            }
        }

        /// <summary>
        /// Remove the Orleans files from the machine for testing.
        /// </summary>
        /// <param name="deployConfigFile">The configuration file which specifies the location of the Orleans folder.</param>
        private void CleanOrleansManually(string deployConfigFile)
        {
            if (Directory.Exists(factoryCacheFolder))
            {
                Directory.Delete(factoryCacheFolder, true);
            }

            string configFileFullPath = Path.Combine(orleansConfigFolder, deployConfigFile);

            string orleansFolder = GetOrleansFolderFromConfig(configFileFullPath);
            if (Directory.Exists(orleansFolder))
            {
                Directory.Delete(orleansFolder, true);
            }
        }

        /// <summary>
        /// Copy all the files and directories from the source directory to the target.
        /// </summary>
        /// <param name="sourceDirectory">The directory where the original files are located.</param>
        /// <param name="targetDirectory">The directory to place the copies in.</param>
        private static void CopyAll(string sourceDirectory, string targetDirectory)
        {
            if (!Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            // Copy all the files from the source to the target
            string[] orleansFiles = Directory.GetFiles(sourceDirectory);
            foreach (string orleansFile in orleansFiles)
            {
                string targetFileName = Path.Combine(targetDirectory, Path.GetFileName(orleansFile));
                // We don't need to copy the .pdb files and they cause problems.
                if (string.Compare(Path.GetExtension(orleansFile), ".pdb", true) != 0)
                {
                    File.Copy(orleansFile, targetFileName, true);
                }
            }
            string[] outSubDirs = Directory.GetDirectories(sourceDirectory);
            foreach (string subDir in outSubDirs)
            {
                string targetSubDir = Path.Combine(targetDirectory, subDir.Substring(subDir.LastIndexOf('\\') + 1));
                CopyAll(subDir, targetSubDir);
            }
        }

        /// <summary>
        /// Gets the path to the Orleans folder from the named configuration file.
        /// </summary>
        /// <param name="configFile">The name of the configuration file to load.</param>
        /// <returns>The path to the Orleans folder found in the configuration file.</returns>
        public string GetOrleansFolderFromConfig(string configFile)
        {
            // <TargetLocation Path="C:\Orleans" />
            string pathValue = string.Empty;
            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), configFile)))
            {
                Assert.Inconclusive("Cannot Run Test: Could not find configuration file {0}", configFile);
            }
            XDocument configDoc = XDocument.Load(configFile);
            Assert.IsNotNull(configDoc, string.Format("Could not load test configuration file: {0}", configFile));
            XNamespace deployNamespace = "urn:xcg-deployment";
            XElement targetElement = configDoc.Root.Element(deployNamespace + "TargetLocation");
            Assert.IsNotNull(targetElement, "Could not find the TargetLocation element in the configuration file: {0}", configFile);
            XAttribute pathAttribute = targetElement.Attribute("Path");
            Assert.IsNotNull(pathAttribute, "The Path attribute of the TargetLocation element in the configuration file is missing or does not have a value : {0}", configFile);
            pathValue = pathAttribute.Value;
            return pathValue;
        }

        /// <summary>
        /// Sets up a folder structure for the unit test that includes a dedicated deployment directory.
        /// </summary>
        /// <param name="testRootFolder">A name for the test.  Don't include spaces, or any Filename\Path characters.</param>
        private static string PrepareTestFolderStructure(string testRootFolder)
        {
            string deployFolder = BuildFolderTree(testRootFolder);
            // Copy the deployment files to the deployment directory.
            CopyAll(preTestFolder, deployFolder);
            // Use an absolute path to avoid ambiguity.
            string configFileFullPath = Path.Combine(deployFolder, "Configuration\\TestRelativePaths\\Deploy-RelativePaths.xml");

            // Now copy the OrleansConfiguration.xml file to the deploy directory.
            string orleansConfigFilePath = Path.Combine(Path.GetDirectoryName(configFileFullPath), "OrleansConfiguration.xml");
            string configFileTargetPath = Path.Combine(deployFolder, "OrleansConfiguration.xml");
            File.Copy(orleansConfigFilePath, configFileTargetPath, true);

            return deployFolder;
        }

        /// <summary>
        /// Runs the indicated PowerShell script and returns the results.  Prefix script names with .\\ but do
        /// not prefix PowerShell commands.
        /// </summary>
        /// <param name="powershellCommand">
        ///     The name of the PowerShell script to run, assumed to be in the current folder. Prefix script
        ///     names with .// to differentiate between a script and a command.</param>
        /// <param name="scriptParameters">Any parameters to be passed to the script.</param>
        /// <returns>A collection of PSObjects that contain the results of running the script.</returns>
        private Collection<PSObject> RunPowerShellCommand(string powershellCommand, params string[] scriptParameters)
        {
            Collection<PSObject> results;
            using (Runspace runspace = RunspaceFactory.CreateRunspace())
            {
                runspace.Open();

                using (Pipeline pipeline = runspace.CreatePipeline())
                {
                    //Invoke-Command -ScriptBlock {cd C:\Projects\Orleans\Binaries\Deployment\Orleans; Invoke-Expression -Command ".\CleanOrleansSilos.ps1"}
                    StringBuilder powerShellCommand = new StringBuilder(string.Format("Invoke-Expression -Command (\"{0}", powershellCommand));
                    // Tack on any parameters.
                    foreach (string scriptParameter in scriptParameters)
                    {
                        powerShellCommand.AppendFormat(" {0}", scriptParameter);
                    }
                    powerShellCommand.AppendFormat("\")");
                    pipeline.Commands.AddScript(powerShellCommand.ToString());

                    results = pipeline.Invoke();
                }

                runspace.Close();
            }
            return results;
        }

        /// <summary>
        /// Stops all processes of the given name.
        /// </summary>
        /// <param name="processName">The name of the process to stop.</param>
        private static void StopProcesses(string processName)
        {
            Process[] orleansHostTestProcess = Process.GetProcessesByName(processName);
            foreach (Process orleansHostProcess in orleansHostTestProcess)
            {
                orleansHostProcess.Kill();
            }

            // Pause to let the process termination complete.
            System.Threading.Thread.Sleep(5000);

            // Confirm that the processes were all stopped.
            orleansHostTestProcess = Process.GetProcessesByName(processName);
            if (orleansHostTestProcess.Count() > 0)
            {
                // Try again.
                foreach (Process orleansHostProcess in orleansHostTestProcess)
                {
                    orleansHostProcess.Kill();
                }
                orleansHostTestProcess = Process.GetProcessesByName(processName);
                if (orleansHostTestProcess.Count() > 0)
                {
                    // If we still didn't stop them, punt.
                    throw new Exception("Could not stop all OrleansHost processes.");
                }
            }
        }

        /// <summary>
        /// Run the .\DeployOrleansSilos.ps1 script using the supplied configuration file, and test the results.
        /// </summary>
        /// <param name="testName">A name for the test.  Don't include spaces, or any Filename\Path characters.</param>
        /// <param name="deploymentConfigFile">The configuration file to use in the test.</param>
        private void TestOrleansDeployment(string testRootFolder, string deploymentConfigFile)
        {
            string testFolder = PrepareTestFolderStructure(testRootFolder);

            // Save the current folder so we can restore it after the test.
            Environment.CurrentDirectory = testFolder;

            Collection<PSObject> results = RunPowerShellCommand(".\\DeployOrleansSilos.ps1", deploymentConfigFile);
            StringBuilder resultStrings = new StringBuilder();
            foreach (PSObject result in results)
            {
                resultStrings.AppendLine(result.ToString());
            }
            string r = resultStrings.ToString();

            System.Threading.Thread.Sleep(9000);
            Process[] orleansPostTestProcess = Process.GetProcessesByName("OrleansHost");
            Assert.IsTrue(orleansPostTestProcess.Count() > 0, "OrleansHost is not started.");
        }

        private bool RunningAsAdmin()
        {
            // Check user is Administrator and has granted UAC elevation permission to run this app
            WindowsIdentity userIdent = WindowsIdentity.GetCurrent();
            WindowsPrincipal userPrincipal = new WindowsPrincipal(userIdent);
            return userPrincipal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        #endregion Support Methods
    }
}
