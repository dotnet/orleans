using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;
using Orleans.TestingHost;
using UnitTests.StorageTests;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable RedundantTypeArgumentsOfMethod
// ReSharper disable CheckNamespace
// ReSharper disable ConvertToConstant.Local

namespace UnitTests
{
    public class ConfigTests : IDisposable
    {
        private readonly ITestOutputHelper output;

        public ConfigTests(ITestOutputHelper output)
        {
            this.output = output;
            TraceLogger.UnInitialize();
            GrainClient.Uninitialize();
            GrainClient.TestOnlyNoConnect = false;
        }

        public void Dispose()
        {
            TraceLogger.UnInitialize();
            GrainClient.Uninitialize();
            GrainClient.TestOnlyNoConnect = false;
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_ParseTimeSpan()
        {
            string str = "1ms";
            TimeSpan ts = TimeSpan.FromMilliseconds(1);
            Assert.AreEqual(ts, ConfigUtilities.ParseTimeSpan(str, str), str);

            str = "2s";
            ts = TimeSpan.FromSeconds(2);
            Assert.AreEqual(ts, ConfigUtilities.ParseTimeSpan(str, str), str);

            str = "3m";
            ts = TimeSpan.FromMinutes(3);
            Assert.AreEqual(ts, ConfigUtilities.ParseTimeSpan(str, str), str);

            str = "4hr";
            ts = TimeSpan.FromHours(4);
            Assert.AreEqual(ts, ConfigUtilities.ParseTimeSpan(str, str), str);

            str = "5"; // Default unit is seconds
            ts = TimeSpan.FromSeconds(5);
            Assert.AreEqual(ts, ConfigUtilities.ParseTimeSpan(str, str), str);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_NewConfigTest()
        {
            TextReader input = File.OpenText("Config_TestSiloConfig.xml");
            ClusterConfiguration config = new ClusterConfiguration();
            config.Load(input);
            input.Close();

            Assert.AreEqual<int>(2, config.Globals.SeedNodes.Count, "Seed node count is incorrect");
            Assert.AreEqual<IPEndPoint>(new IPEndPoint(IPAddress.Loopback, 11111), config.Globals.SeedNodes[0], "First seed node is set incorrectly");
            Assert.AreEqual<IPEndPoint>(new IPEndPoint(IPAddress.IPv6Loopback, 22222), config.Globals.SeedNodes[1], "Second seed node is set incorrectly");

            Assert.AreEqual<int>(12345, config.Defaults.Port, "Default port is set incorrectly");
            Assert.AreEqual<string>("UnitTests.General.TestStartup,Tester", config.Defaults.StartupTypeName);

            NodeConfiguration nc;
            bool hasNodeConfig = config.TryGetNodeConfigurationForSilo("Node1", out nc);
            Assert.IsTrue(hasNodeConfig, "Node Node1 has config");
            Assert.AreEqual<int>(11111, nc.Port, "Port is set incorrectly for node Node1");
            Assert.IsTrue(nc.IsPrimaryNode, "Node1 should be primary node");
            Assert.IsTrue(nc.IsSeedNode, "Node1 should be seed node");
            Assert.IsFalse(nc.IsGatewayNode, "Node1 should not be gateway node");
            Assert.AreEqual<string>("UnitTests.General.TestStartup,Tester", nc.StartupTypeName, "Startup type should be copied automatically");

            hasNodeConfig = config.TryGetNodeConfigurationForSilo("Node2", out nc);
            Assert.IsTrue(hasNodeConfig, "Node Node2 has config");
            Assert.AreEqual<int>(22222, nc.Port, "Port is set incorrectly for node Node2");
            Assert.IsFalse(nc.IsPrimaryNode, "Node2 should not be primary node");
            Assert.IsTrue(nc.IsSeedNode, "Node2 should be seed node");
            Assert.IsTrue(nc.IsGatewayNode, "Node2 should be gateway node");

            hasNodeConfig = config.TryGetNodeConfigurationForSilo("Store", out nc);
            Assert.IsTrue(hasNodeConfig, "Node Store has config");
            Assert.AreEqual<int>(12345, nc.Port, "IP port is set incorrectly for node Store");
            Assert.IsFalse(nc.IsPrimaryNode, "Store should not be primary node");
            Assert.IsFalse(nc.IsSeedNode, "Store should not be seed node");
            Assert.IsFalse(nc.IsGatewayNode, "Store should not be gateway node");

            //IPAddress[] ips = Dns.GetHostAddresses("");
            //IPEndPoint ep = new IPEndPoint(IPAddress.Loopback, 12345);
            //for (int i = 0; i < ips.Length; i++)
            //{
            //    if ((ips[i].AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) && !IPAddress.Loopback.Equals(ips[i]))
            //    {
            //        ep = new IPEndPoint(ips[i], 12345);
            //        break;
            //    }
            //}

            //Assert.AreEqual<IPEndPoint>(ep, nc.Endpoint, "IP endpoint is set incorrectly for node Store");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void LogFileName()
        {
            var oc = new ClusterConfiguration();
            oc.StandardLoad();
            NodeConfiguration n = oc.CreateNodeConfigurationForSilo("Node1");
            string fname = n.TraceFileName;
            Assert.IsNotNull(fname);
            Assert.IsFalse(fname.Contains(":"), "Log file name should not contain colons.");

            // Check that .NET is happy with the file name
            var f = new FileInfo(fname);
            Assert.IsNotNull(f.Name);
            Assert.AreEqual(fname, f.Name);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void NodeLogFileName()
        {
            string siloName = "MyNode1";
            string baseLogFileName = siloName + ".log";
            string baseLogFileNamePlusOne = siloName + "-1.log";
            string expectedLogFileName = baseLogFileName;
            string configFileName = "Config_NonTimestampedLogFileNames.xml";

            if (File.Exists(baseLogFileName)) File.Delete(baseLogFileName);
            if (File.Exists(expectedLogFileName)) File.Delete(expectedLogFileName);

            var config = new ClusterConfiguration();
            config.LoadFromFile(configFileName);
            NodeConfiguration n = config.CreateNodeConfigurationForSilo(siloName);
            string fname = n.TraceFileName;

            Assert.AreEqual(baseLogFileName, fname);

            TraceLogger.Initialize(n);

            Assert.IsTrue(File.Exists(baseLogFileName), "Base name log file exists: " + baseLogFileName);
            Assert.IsTrue(File.Exists(expectedLogFileName), "Expected name log file exists: " + expectedLogFileName);
            Assert.IsFalse(File.Exists(baseLogFileNamePlusOne), "Munged log file exists: " + baseLogFileNamePlusOne);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void NodeLogFileNameAlreadyExists()
        {
            string siloName = "MyNode2";
            string baseLogFileName = siloName + ".log";
            string baseLogFileNamePlusOne = siloName + "-1.log";
            string expectedLogFileName = baseLogFileName;
            string configFileName = "Config_NonTimestampedLogFileNames.xml";

            if (File.Exists(baseLogFileName)) File.Delete(baseLogFileName);
            if (File.Exists(expectedLogFileName)) File.Delete(expectedLogFileName);

            if (!File.Exists(baseLogFileName)) File.Create(baseLogFileName).Close();

            var config = new ClusterConfiguration();
            config.LoadFromFile(configFileName);
            NodeConfiguration n = config.CreateNodeConfigurationForSilo(siloName);
            string fname = n.TraceFileName;

            Assert.AreEqual(baseLogFileName, fname);

            TraceLogger.Initialize(n);

            Assert.IsTrue(File.Exists(baseLogFileName), "Base name log file exists: " + baseLogFileName);
            Assert.IsTrue(File.Exists(expectedLogFileName), "Expected name log file exists: " + expectedLogFileName);
            Assert.IsFalse(File.Exists(baseLogFileNamePlusOne), "Munged log file exists: " + baseLogFileNamePlusOne);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void LogFile_Write_AlreadyExists()
        {
            const string siloName = "MyNode3";
            const string configFileName = "Config_NonTimestampedLogFileNames.xml";

            string logFileName = siloName + ".log";
            FileInfo fileInfo = new FileInfo(logFileName);

            CreateIfNotExists(fileInfo);
            Assert.IsTrue(fileInfo.Exists, "Log file should exist: " + fileInfo.FullName);

            long initialSize = fileInfo.Length;

            var config = new ClusterConfiguration();
            config.LoadFromFile(configFileName);
            NodeConfiguration n = config.CreateNodeConfigurationForSilo(siloName);
            string fname = n.TraceFileName;

            Assert.AreEqual(logFileName, fname);

            TraceLogger.Initialize(n);

            Assert.IsTrue(File.Exists(fileInfo.FullName), "Log file exists - before write: " + fileInfo.FullName);

            TraceLogger myLogger = TraceLogger.GetLogger("MyLogger", TraceLogger.LoggerType.Application);

            myLogger.Info("Write something");
            TraceLogger.Flush();

            fileInfo.Refresh(); // Need to refresh cached view of FileInfo

            Assert.IsTrue(fileInfo.Exists, "Log file exists - after write: " + fileInfo.FullName);

            long currentSize = fileInfo.Length;

            Assert.IsTrue(currentSize > initialSize, "Log file {0} should have been written to: Initial size = {1} Current size = {2}", logFileName, initialSize, currentSize);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void LogFile_Write_NotExists()
        {
            const string siloName = "MyNode4";
            const string configFileName = "Config_NonTimestampedLogFileNames.xml";

            string logFileName = siloName + ".log";
            FileInfo fileInfo = new FileInfo(logFileName);

            DeleteIfExists(fileInfo);

            Assert.IsFalse(File.Exists(fileInfo.FullName), "Log file should not exist: " + fileInfo.FullName);

            long initialSize = 0;

            var config = new ClusterConfiguration();
            config.LoadFromFile(configFileName);
            NodeConfiguration n = config.CreateNodeConfigurationForSilo(siloName);
            string fname = n.TraceFileName;

            Assert.AreEqual(logFileName, fname);

            TraceLogger.Initialize(n);

            Assert.IsTrue(File.Exists(fileInfo.FullName), "Log file exists - before write: " + fileInfo.FullName);

            TraceLogger myLogger = TraceLogger.GetLogger("MyLogger", TraceLogger.LoggerType.Application);

            myLogger.Info("Write something");
            TraceLogger.Flush();

            fileInfo.Refresh(); // Need to refresh cached view of FileInfo

            Assert.IsTrue(fileInfo.Exists, "Log file exists - after write: " + fileInfo.FullName);

            long currentSize = fileInfo.Length;

            Assert.IsTrue(currentSize > initialSize, "Log file {0} should have been written to: Initial size = {1} Current size = {2}", logFileName, initialSize, currentSize);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void LogFile_Create()
        {
            const string siloName = "MyNode5";

            string logFileName = siloName + ".log";
            FileInfo fileInfo = new FileInfo(logFileName);

            DeleteIfExists(fileInfo);

            bool fileExists = fileInfo.Exists;
            Assert.IsFalse(fileExists, "Log file should not exist: " + fileInfo.FullName);

            CreateIfNotExists(fileInfo);

            fileExists = fileInfo.Exists;
            Assert.IsTrue(fileExists, "Log file should exist: " + fileInfo.FullName);

            long initialSize = fileInfo.Length;

            Assert.AreEqual(0, initialSize, "Log file {0} should be empty. Current size = {1}", logFileName, initialSize);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ClientConfig_Default_ToString()
        {
            var cfg = new ClientConfiguration();
            var str = cfg.ToString();
            Assert.IsNotNull(str, "ClientConfiguration.ToString");
            output.WriteLine(str);
            Assert.IsNull(cfg.SourceFile, "SourceFile");
            //Assert.IsNull(cfg.TraceFileName, "TraceFileName");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ClientConfig_TraceFileName_Blank()
        {
            var cfg = new ClientConfiguration();
            cfg.TraceFileName = string.Empty;
            output.WriteLine(cfg.ToString());

            cfg.TraceFileName = null;
            output.WriteLine(cfg.ToString());
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ClientConfig_TraceFilePattern_Blank()
        {
            var cfg = new ClientConfiguration();
            cfg.TraceFilePattern = string.Empty;
            output.WriteLine(cfg.ToString());
            Assert.IsNull(cfg.TraceFileName, "TraceFileName should be null");

            cfg.TraceFilePattern = null;
            output.WriteLine(cfg.ToString());
            Assert.IsNull(cfg.TraceFileName, "TraceFileName should be null");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ServerConfig_TraceFileName_Blank()
        {
            var cfg = new NodeConfiguration();
            cfg.TraceFileName = string.Empty;
            output.WriteLine(cfg.ToString());

            cfg.TraceFileName = null;
            output.WriteLine(cfg.ToString());
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ServerConfig_TraceFilePattern_Blank()
        {
            var cfg = new NodeConfiguration();
            cfg.TraceFilePattern = string.Empty;
            output.WriteLine(cfg.ToString());
            Assert.IsNull(cfg.TraceFileName, "TraceFileName should be null");

            cfg.TraceFilePattern = null;
            output.WriteLine(cfg.ToString());
            Assert.IsNull(cfg.TraceFileName, "TraceFileName should be null");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Logger")]
        public void ClientConfig_LogConsumers()
        {
            TraceLogger.UnInitialize();

            string filename = "Config_LogConsumers-ClientConfiguration.xml";

            var cfg = ClientConfiguration.LoadFromFile(filename);
            Assert.AreEqual(filename, cfg.SourceFile);

            TraceLogger.Initialize(cfg);
            Assert.AreEqual(1, TraceLogger.LogConsumers.Count,
                "Number of log consumers: " + string.Join(",", TraceLogger.LogConsumers));
            Assert.AreEqual(typeof(DummyLogConsumer).FullName, TraceLogger.LogConsumers.Last().GetType().FullName, "Log consumer type #1");

            Assert.AreEqual(1, TraceLogger.TelemetryConsumers.Count,
                "Number of telemetry consumers: " + string.Join(",", TraceLogger.TelemetryConsumers));
            Assert.AreEqual(typeof(TraceTelemetryConsumer).FullName, TraceLogger.TelemetryConsumers.First().GetType().FullName, "TelemetryConsumers consumer type #1");

        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Logger")]
        public void ServerConfig_LogConsumers()
        {
            TraceLogger.UnInitialize();

            string filename = "Config_LogConsumers-OrleansConfiguration.xml";

            var cfg = new ClusterConfiguration();
            cfg.LoadFromFile(filename);
            Assert.AreEqual(filename, cfg.SourceFile);

            TraceLogger.Initialize(cfg.CreateNodeConfigurationForSilo("Primary"));

            var actualLogConsumers = TraceLogger.LogConsumers.Select(x => x.GetType()).ToList();
            Xunit.Assert.Contains(typeof(DummyLogConsumer), actualLogConsumers);
            Assert.AreEqual(1, actualLogConsumers.Count);

            var actualTelemetryConsumers = TraceLogger.TelemetryConsumers.Select(x => x.GetType()).ToList();
            Xunit.Assert.Contains(typeof(TraceTelemetryConsumer), actualTelemetryConsumers);
            Xunit.Assert.Contains(typeof(ConsoleTelemetryConsumer), actualTelemetryConsumers);
            Assert.AreEqual(2, actualTelemetryConsumers.Count);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Limits")]
        public void Limits_ClientConfig()
        {
            const string filename = "Config_LogConsumers-ClientConfiguration.xml";
            var config = ClientConfiguration.LoadFromFile(filename);

            string limitName;
            LimitValue limit;
            //Assert.IsTrue(config.LimitManager.LimitValues.Count >= 3, "Number of LimitValues: " + string.Join(",", config.LimitValues));
            for (int i = 1; i <= 3; i++)
            {
                limitName = "Limit" + i;
                limit = config.LimitManager.GetLimit(limitName);
                Assert.IsNotNull(limit);
                Assert.AreEqual(limitName, limit.Name, "Limit name " + i);
                Assert.AreEqual(i, limit.SoftLimitThreshold, "Soft limit " + i);
                Assert.AreEqual(2 * i, limit.HardLimitThreshold, "Hard limit " + i);
            }

            limitName = "NoHardLimit";
            limit = config.LimitManager.GetLimit(limitName);
            Assert.IsNotNull(limit);
            Assert.AreEqual(limitName, limit.Name, "Limit name " + limitName);
            Assert.AreEqual(4, limit.SoftLimitThreshold, "Soft limit " + limitName);
            Assert.AreEqual(0, limit.HardLimitThreshold, "Hard limit " + limitName);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Limits")]
        public void Limits_ServerConfig()
        {
            const string filename = "Config_LogConsumers-OrleansConfiguration.xml";
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            NodeConfiguration config;
            bool hasNodeConfig = orleansConfig.TryGetNodeConfigurationForSilo("Primary", out config);
            Assert.IsTrue(hasNodeConfig, "Node Primary has config");


            string limitName;
            LimitValue limit;
            //Assert.IsTrue(config.LimitManager.LimitValues.Count >= 3, "Number of LimitValues: " + string.Join(",", config.LimitValues));
            for (int i = 1; i <= 3; i++)
            {
                limitName = "Limit" + i;
                limit = config.LimitManager.GetLimit(limitName);
                Assert.IsNotNull(limit);
                Assert.AreEqual(limitName, limit.Name, "Limit name " + i);
                Assert.AreEqual(i, limit.SoftLimitThreshold, "Soft limit " + i);
                Assert.AreEqual(2 * i, limit.HardLimitThreshold, "Hard limit " + i);
            }

            limitName = "NoHardLimit";
            limit = config.LimitManager.GetLimit(limitName);
            Assert.IsNotNull(limit);
            Assert.AreEqual(limitName, limit.Name, "Limit name " + limitName);
            Assert.AreEqual(4, limit.SoftLimitThreshold, "Soft limit " + limitName);
            Assert.AreEqual(0, limit.HardLimitThreshold, "Hard limit " + limitName);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Limits")]
        public void Limits_ClientConfig_NotSpecified()
        {
            const string filename = "Config_LogConsumers-ClientConfiguration.xml";
            var config = ClientConfiguration.LoadFromFile(filename);

            string limitName = "NotPresent";
            LimitValue limit = config.LimitManager.GetLimit(limitName);
            Assert.AreEqual(0, limit.SoftLimitThreshold);
            Assert.AreEqual(0, limit.HardLimitThreshold);
            Assert.AreEqual(limitName, limit.Name);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Limits")]
        public void Limits_ServerConfig_NotSpecified()
        {
            const string filename = "Config_LogConsumers-OrleansConfiguration.xml";
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            NodeConfiguration config;
            bool hasNodeConfig = orleansConfig.TryGetNodeConfigurationForSilo("Primary", out config);
            Assert.IsTrue(hasNodeConfig, "Node Primary has config");


            string limitName = "NotPresent";
            LimitValue limit = config.LimitManager.GetLimit(limitName);
            Assert.AreEqual(0, limit.SoftLimitThreshold);
            Assert.AreEqual(0, limit.HardLimitThreshold);
            Assert.AreEqual(limitName, limit.Name);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Limits")]
        public void Limits_LimitsManager_ServerConfig()
        {
            const string filename = "Config_LogConsumers-OrleansConfiguration.xml";
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            NodeConfiguration config;
            bool hasNodeConfig = orleansConfig.TryGetNodeConfigurationForSilo("Primary", out config);
            Assert.IsTrue(hasNodeConfig, "Node Primary has config");


            string limitName;
            LimitValue limit;
            for (int i = 1; i <= 3; i++)
            {
                limitName = "Limit" + i;
                limit = config.LimitManager.GetLimit(limitName);
                Assert.IsNotNull(limit);
                Assert.AreEqual(limitName, limit.Name, "Limit name " + i);
                Assert.AreEqual(i, limit.SoftLimitThreshold, "Soft limit " + i);
                Assert.AreEqual(2 * i, limit.HardLimitThreshold, "Hard limit " + i);
            }

            limitName = "NoHardLimit";
            limit = config.LimitManager.GetLimit(limitName);
            Assert.IsNotNull(limit);
            Assert.AreEqual(limitName, limit.Name, "Limit name " + limitName);
            Assert.AreEqual(4, limit.SoftLimitThreshold, "Soft limit " + limitName);
            Assert.AreEqual(0, limit.HardLimitThreshold, "No Hard limit " + limitName);

            limitName = "NotPresent";
            limit = config.LimitManager.GetLimit(limitName);
            Assert.IsNotNull(limit);
            Assert.AreEqual(limitName, limit.Name, "Limit name " + limitName);
            Assert.AreEqual(0, limit.SoftLimitThreshold, "No Soft limit " + limitName);
            Assert.AreEqual(0, limit.HardLimitThreshold, "No Hard limit " + limitName);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Limits")]
        public void Limits_LimitsManager_ClientConfig()
        {
            const string filename = "Config_LogConsumers-ClientConfiguration.xml";
            var config = ClientConfiguration.LoadFromFile(filename);

            string limitName;
            LimitValue limit;
            for (int i = 1; i <= 3; i++)
            {
                limitName = "Limit" + i;
                limit = config.LimitManager.GetLimit(limitName);
                Assert.IsNotNull(limit);
                Assert.AreEqual(limitName, limit.Name, "Limit name " + i);
                Assert.AreEqual(i, limit.SoftLimitThreshold, "Soft limit " + i);
                Assert.AreEqual(2 * i, limit.HardLimitThreshold, "Hard limit " + i);
            }

            limitName = "NoHardLimit";
            limit = config.LimitManager.GetLimit(limitName);
            Assert.IsNotNull(limit);
            Assert.AreEqual(limitName, limit.Name, "Limit name " + limitName);
            Assert.AreEqual(4, limit.SoftLimitThreshold, "Soft limit " + limitName);
            Assert.AreEqual(0, limit.HardLimitThreshold, "No Hard limit " + limitName);

            limitName = "NotPresent";
            limit = config.LimitManager.GetLimit(limitName);
            Assert.IsNotNull(limit);
            Assert.AreEqual(limitName, limit.Name, "Limit name " + limitName);
            Assert.AreEqual(0, limit.SoftLimitThreshold, "No Soft limit " + limitName);
            Assert.AreEqual(0, limit.HardLimitThreshold, "No Hard limit " + limitName);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Limits")]
        public void Limits_ClientConfig_NotSpecified_WithDefault()
        {
            const string filename = "Config_LogConsumers-ClientConfiguration.xml";
            var config = ClientConfiguration.LoadFromFile(filename);

            string limitName;
            LimitValue limit;
            for (int i = 1; i <= 3; i++)
            {
                limitName = "NotPresent" + i;
                limit = config.LimitManager.GetLimit(limitName, i);
                Assert.IsNotNull(limit);
                Assert.AreEqual(limitName, limit.Name, "Limit name " + i);
                Assert.AreEqual(i, limit.SoftLimitThreshold, "Soft limit " + i);
                Assert.AreEqual(0, limit.HardLimitThreshold, "No Hard limit " + i);
            }
            for (int i = 1; i <= 3; i++)
            {
                limitName = "NotPresent" + i;
                limit = config.LimitManager.GetLimit(limitName, i, 2 * i);
                Assert.IsNotNull(limit);
                Assert.AreEqual(limitName, limit.Name, "Limit name " + i);
                Assert.AreEqual(i, limit.SoftLimitThreshold, "Soft limit " + i);
                Assert.AreEqual(2 * i, limit.HardLimitThreshold, "Hard limit " + i);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Limits")]
        public void Limits_ServerConfig_NotSpecified_WithDefault()
        {
            const string filename = "Config_LogConsumers-OrleansConfiguration.xml";
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            NodeConfiguration config;
            bool hasNodeConfig = orleansConfig.TryGetNodeConfigurationForSilo("Primary", out config);
            Assert.IsTrue(hasNodeConfig, "Node Primary has config");

            string limitName;
            LimitValue limit;
            for (int i = 1; i <= 3; i++)
            {
                limitName = "NotPresent" + i;
                limit = config.LimitManager.GetLimit(limitName, i);
                Assert.IsNotNull(limit);
                Assert.AreEqual(limitName, limit.Name, "Limit name " + i);
                Assert.AreEqual(i, limit.SoftLimitThreshold, "Soft limit " + i);
                Assert.AreEqual(0, limit.HardLimitThreshold, "No Hard limit " + i);
            }
            for (int i = 1; i <= 3; i++)
            {
                limitName = "NotPresent" + i;
                limit = config.LimitManager.GetLimit(limitName, i, 2 * i);
                Assert.IsNotNull(limit);
                Assert.AreEqual(limitName, limit.Name, "Limit name " + i);
                Assert.AreEqual(i, limit.SoftLimitThreshold, "Soft limit " + i);
                Assert.AreEqual(2 * i, limit.HardLimitThreshold, "Hard limit " + i);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void Config_AzureConnectionInfo()
        {
            string azureConnectionStringInput =
                @"DefaultEndpointsProtocol=https;AccountName=test;AccountKey=q-SOMEKEY-==";
            output.WriteLine("Input = " + azureConnectionStringInput);
            string azureConnectionString = ConfigUtilities.PrintDataConnectionInfo(azureConnectionStringInput);
            output.WriteLine("Output = " + azureConnectionString);
            Assert.IsTrue(azureConnectionString.EndsWith("AccountKey=<--SNIP-->", StringComparison.InvariantCultureIgnoreCase),
                "Removed account key info from Azure connection string " + azureConnectionString);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("SqlServer")]
        public void Config_SqlConnectionInfo()
        {
            string sqlConnectionStringInput =
                @"Server=myServerName\myInstanceName;Database=myDataBase;User Id=myUsername;Password=myPassword";
            output.WriteLine("Input = " + sqlConnectionStringInput);
            string sqlConnectionString = ConfigUtilities.PrintSqlConnectionString(sqlConnectionStringInput);
            output.WriteLine("Output = " + sqlConnectionString);
            Assert.IsTrue(sqlConnectionString.EndsWith("Password=<--SNIP-->", StringComparison.InvariantCultureIgnoreCase),
                "Removed password info from SqlServer connection string " + sqlConnectionString);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void Config_StorageProvider_Azure1()
        {
            const string filename = "Config_StorageProvider_Azure1.xml";
            const int numProviders = 1;
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            var providerConfigs = orleansConfig.Globals.ProviderConfigurations["Storage"];
            Assert.IsNotNull(providerConfigs, "Null provider configs");
            Assert.IsNotNull(providerConfigs.Providers, "Null providers");
            Assert.AreEqual(numProviders, providerConfigs.Providers.Count, "Num provider configs");

            ProviderConfiguration pCfg = (ProviderConfiguration)providerConfigs.Providers.Values.ToList()[0];
            Assert.AreEqual("orleanstest1", pCfg.Name, "Provider name #1");
            Assert.AreEqual("AzureTable", pCfg.Type, "Provider type #1");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void Config_StorageProvider_Azure2()
        {
            const string filename = "Config_StorageProvider_Azure2.xml";
            const int numProviders = 2;
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            var providerConfigs = orleansConfig.Globals.ProviderConfigurations["Storage"];
            Assert.IsNotNull(providerConfigs, "Null provider configs");
            Assert.IsNotNull(providerConfigs.Providers, "Null providers");
            Assert.AreEqual(numProviders, providerConfigs.Providers.Count, "Num provider configs");

            ProviderConfiguration pCfg = (ProviderConfiguration)providerConfigs.Providers.Values.ToList()[0];
            Assert.AreEqual("orleanstest1", pCfg.Name, "Provider name #1");
            Assert.AreEqual("AzureTable", pCfg.Type, "Provider type #1");

            pCfg = (ProviderConfiguration)providerConfigs.Providers.Values.ToList()[1];
            Assert.AreEqual("orleanstest2", pCfg.Name, "Provider name #2");
            Assert.AreEqual("AzureTable", pCfg.Type, "Provider type #2");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Providers")]
        public void Config_StorageProvider_NoConfig()
        {
            const string filename = "Config_StorageProvider_Memory2.xml";
            const int numProviders = 2;
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            var providerConfigs = orleansConfig.Globals.ProviderConfigurations["Storage"];
            Assert.IsNotNull(providerConfigs, "Null provider configs");
            Assert.IsNotNull(providerConfigs.Providers, "Null providers");
            Assert.AreEqual(numProviders, providerConfigs.Providers.Count, "Num provider configs");
            for (int i = 0; i < providerConfigs.Providers.Count; i++)
            {
                IProviderConfiguration provider = providerConfigs.Providers.Values.ToList()[i];
                Assert.AreEqual("test" + i, ((ProviderConfiguration)provider).Name, "Provider name #" + i);
                Assert.AreEqual(typeof(MockStorageProvider).FullName, ((ProviderConfiguration)provider).Type, "Provider type #" + i);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Providers")]
        public void Config_StorageProvider_SomeConfig()
        {
            const string filename = "Config_StorageProvider_SomeConfig.xml";
            const int numProviders = 2;
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            var providerConfigs = orleansConfig.Globals.ProviderConfigurations["Storage"];
            Assert.IsNotNull(providerConfigs, "Null provider configs");
            Assert.IsNotNull(providerConfigs.Providers, "Null providers");
            Assert.AreEqual(numProviders, providerConfigs.Providers.Count, "Num provider configs");
            for (int i = 0; i < providerConfigs.Providers.Count; i++)
            {
                IProviderConfiguration provider = providerConfigs.Providers.Values.ToList()[i];
                Assert.AreEqual("config" + i, ((ProviderConfiguration)provider).Name, "Provider name #" + i);
                Assert.AreEqual(typeof(MockStorageProvider).FullName, ((ProviderConfiguration)provider).Type, "Provider type #" + i);
                for (int j = 0; j < 2; j++)
                {
                    int num = 2 * i + j;
                    string key = "Prop" + num;
                    string cfg = provider.Properties[key];
                    Assert.IsNotNull(cfg, "Null config value " + key);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(cfg), "Blank config value " + key);
                    Assert.AreEqual(num.ToString(CultureInfo.InvariantCulture), cfg, "Config value {0} = {1}", key, cfg);
                }
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_AdditionalAssemblyPaths_Config()
        {
            const string filename = "Config_AdditionalAssemblies.xml";
            const int numPaths = 2;
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);

            Assert.IsNotNull(orleansConfig.Defaults.AdditionalAssemblyDirectories, "Additional Assembly Dictionary");
            Assert.AreEqual(numPaths, orleansConfig.Defaults.AdditionalAssemblyDirectories.Count, "Additional Assembly count");

        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void Config_StorageProviders_AzureTable_Default()
        {
            const string filename = "Config_StorageProvider_Azure1.xml";

            var config = new ClusterConfiguration();
            config.LoadFromFile(filename);

            output.WriteLine(config.Globals.ToString());

            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.MembershipTableGrain, config.Globals.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain, config.Globals.ReminderServiceType, "ReminderServiceType");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Gateway")]
        public void ClientConfig_Default()
        {
            const string filename = "ClientConfiguration.xml";

            ClientConfiguration config = ClientConfiguration.LoadFromFile(filename);

            output.WriteLine(config);

            Assert.AreEqual(ClientConfiguration.GatewayProviderType.Config, config.GatewayProvider, "GatewayProviderType");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void ClientConfig_ClientInit_FromFile()
        {
            const string filename = "ClientConfig_NewAzure.xml";

            try
            {
                GrainClient.TestOnlyNoConnect = true;

                GrainClient.Initialize(filename);

                ClientConfiguration config = GrainClient.CurrentConfig;

                output.WriteLine(config);

                Assert.IsNotNull(config, "Client.CurrentConfig");

                Assert.AreEqual(filename, Path.GetFileName(config.SourceFile), "ClientConfig.SourceFile");

                Assert.AreEqual(ClientConfiguration.GatewayProviderType.AzureTable, config.GatewayProvider, "GatewayProviderType");
            }
            finally
            {
                GrainClient.TestOnlyNoConnect = false;
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void ClientConfig_AzureInit_FileNotFound()
        {
            const string filename = "ClientConfig_NotFound.xml";
            GrainClient.TestOnlyNoConnect = true;
            try
            {
                Xunit.Assert.Throws<FileNotFoundException>(() =>
                    GrainClient.Initialize(filename));
            }
            finally
            {
                GrainClient.TestOnlyNoConnect = false;
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void ClientConfig_FromFile_FileNotFound()
        {
            const string filename = "ClientConfig_NotFound.xml";
            Xunit.Assert.Throws<FileNotFoundException>(() =>
            ClientConfiguration.LoadFromFile(filename));
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void ServerConfig_FromFile_FileNotFound()
        {
            const string filename = "SiloConfig_NotFound.xml";
            var config = new ClusterConfiguration();
            Xunit.Assert.Throws<FileNotFoundException>(() =>
                config.LoadFromFile(filename));
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ClientConfig_LoadFrom()
        {
            string filename = "Config_LogConsumers-ClientConfiguration.xml";

            var config = ClientConfiguration.LoadFromFile(filename);

            Assert.IsNotNull(config, "ClientConfiguration null");
            Assert.IsNotNull(config.ToString(), "ClientConfiguration.ToString");

            Assert.AreEqual(filename, Path.GetFileName(config.SourceFile), "ClientConfig.SourceFile");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ServerConfig_LoadFrom()
        {
            string filename = "Config_LogConsumers-OrleansConfiguration.xml";

            var config = new ClusterConfiguration();
            config.LoadFromFile(filename);

            Assert.IsNotNull(config.ToString(), "OrleansConfiguration.ToString");

            Assert.AreEqual(filename, Path.GetFileName(config.SourceFile), "OrleansConfiguration.SourceFile");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("SqlServer")]
        public void ClientConfig_SqlServer()
        {
            const string filename = "DevTestClientConfiguration.xml";

            ClientConfiguration config = ClientConfiguration.LoadFromFile(filename);

            output.WriteLine(config);

            Assert.AreEqual(ClientConfiguration.GatewayProviderType.SqlServer, config.GatewayProvider, "GatewayProviderType");
            Assert.AreEqual(ClientConfiguration.GatewayProviderType.SqlServer, config.GatewayProviderToUse, "GatewayProviderToUse");

            Assert.IsNotNull(config.DataConnectionString, "Connection string should not be null");
            Assert.IsFalse(string.IsNullOrWhiteSpace(config.DataConnectionString), "Connection string should not be blank");

            Assert.IsFalse(config.UseAzureSystemStore, "Should not be using Azure storage");
            Assert.IsTrue(config.UseSqlSystemStore, "Should be using SqlServer storage");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("SqlServer")]
        public void ClientConfig_SqlServer_StatsProvider()
        {
            const string filename = "DevTestClientConfiguration.xml";

            ClientConfiguration config = ClientConfiguration.LoadFromFile(filename);

            output.WriteLine(config);

            Assert.AreEqual(1, config.ProviderConfigurations.Count, "Number of Providers Types");
            Assert.AreEqual("Statistics", config.ProviderConfigurations.Keys.First(), "Client Stats Providers");
            ProviderCategoryConfiguration statsProviders = config.ProviderConfigurations["Statistics"];
            Assert.AreEqual(1, statsProviders.Providers.Count, "Number of Stats Providers");
            Assert.AreEqual("SQL", statsProviders.Providers.Keys.First(), "Stats provider name");
            ProviderConfiguration providerConfig = (ProviderConfiguration)statsProviders.Providers["SQL"];
            // Note: Use string here instead of typeof(SqlStatisticsPublisher).FullName to prevent cascade load of this type
            Assert.AreEqual("Orleans.Providers.SqlServer.SqlStatisticsPublisher", providerConfig.Type, "Stats provider class name");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("SqlServer")]
        public void SiloConfig_SqlServer()
        {
            const string filename = "DevTestServerConfiguration.xml";
            Guid myGuid = Guid.Empty;

            TraceLogger.Initialize(new NodeConfiguration());

            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);

            output.WriteLine(orleansConfig.Globals);

            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.SqlServer, orleansConfig.Globals.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.SqlServer, orleansConfig.Globals.ReminderServiceType, "ReminderServiceType");

            Assert.IsNotNull(orleansConfig.Globals.DataConnectionString, "DataConnectionString should not be null");
            Assert.IsFalse(string.IsNullOrWhiteSpace(orleansConfig.Globals.DataConnectionString), "DataConnectionString should not be blank");

            Assert.IsFalse(orleansConfig.Globals.UseAzureSystemStore, "Should not be using Azure storage");
            Assert.IsTrue(orleansConfig.Globals.UseSqlSystemStore, "Should be using SqlServer storage");

            Assert.AreEqual(orleansConfig.Globals.ServiceId, myGuid, "ServiceId");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("SqlServer")]
        public void SiloConfig_SqlServer_StatsProvider()
        {
            const string filename = "DevTestServerConfiguration.xml";

            var config = new ClusterConfiguration();
            config.LoadFromFile(filename);

            output.WriteLine(config);

            Assert.AreEqual(2, config.Globals.ProviderConfigurations.Count, "Number of Providers Types");
            Assert.IsTrue(config.Globals.ProviderConfigurations.Keys.Contains("Statistics"), "Stats Providers");
            ProviderCategoryConfiguration statsProviders = config.Globals.ProviderConfigurations["Statistics"];
            Assert.AreEqual(1, statsProviders.Providers.Count, "Number of Stats Providers");
            Assert.AreEqual("SQL", statsProviders.Providers.Keys.First(), "Stats provider name");
            ProviderConfiguration providerConfig = (ProviderConfiguration)statsProviders.Providers["SQL"];
            // Note: Use string here instead of typeof(SqlStatisticsPublisher).FullName to prevent cascade load of this type
            Assert.AreEqual("Orleans.Providers.SqlServer.SqlStatisticsPublisher", providerConfig.Type, "Stats provider class name");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void SiloConfig_Azure_Default()
        {
            const string filename = "Config_Azure_Default.xml";

            string deploymentId = "SiloConfig_Azure_Default" + TestConstants.random.Next();
            string connectionString = StorageTestConstants.DataConnectionString;

            var initialConfig = new ClusterConfiguration();
            initialConfig.LoadFromFile(filename);

            output.WriteLine(initialConfig.Globals);

            // Do same code that AzureSilo does for configuring silo host

            var host = new SiloHost("SiloConfig_Azure_Default", initialConfig); // Use supplied config data + Initializes logger configurations
            host.SetSiloType(Silo.SiloType.Secondary);
            ////// Always use Azure table for membership when running silo in Azure
            host.SetSiloLivenessType(GlobalConfiguration.LivenessProviderType.AzureTable);
            host.SetReminderServiceType(GlobalConfiguration.ReminderServiceProviderType.AzureTable);
            host.SetDeploymentId(deploymentId, connectionString);

            ClusterConfiguration siloConfig = host.Config;

            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.AzureTable, siloConfig.Globals.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.AzureTable, siloConfig.Globals.ReminderServiceType, "ReminderServiceType");

            Assert.AreEqual(deploymentId, siloConfig.Globals.DeploymentId, "DeploymentId");
            Assert.AreEqual(connectionString, siloConfig.Globals.DataConnectionString, "DataConnectionString");

            Assert.IsTrue(siloConfig.Globals.UseAzureSystemStore, "Should be using Azure storage");
            Assert.IsFalse(siloConfig.Globals.UseSqlSystemStore, "Should not be using SqlServer storage");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void SiloConfig_Programatic()
        {
            string siloName = "SiloConfig_Programatic";

            var config = new ClusterConfiguration();
            config.Globals.CacheSize = 11;

            var host = new SiloHost(siloName, config); // Use supplied config data + Initializes logger configurations

            ClusterConfiguration siloConfig = host.Config;

            output.WriteLine(siloConfig.Globals);

            Assert.AreEqual("", siloConfig.SourceFile, "SourceFile should be blank for programmatic config");
            Assert.AreEqual(11, siloConfig.Globals.CacheSize, "CacheSize picked up from config object");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ClientConfig_Programatic()
        {
            string deploymentId = "ClientConfig_Programatic";

            var config = new ClientConfiguration();

            config.DeploymentId = deploymentId;
            config.DataConnectionString = StorageTestConstants.DataConnectionString;
            config.GatewayProvider = ClientConfiguration.GatewayProviderType.AzureTable;

            config.PreferedGatewayIndex = 11;

            output.WriteLine(config);

            Assert.AreEqual(null, config.SourceFile, "SourceFile should be blank for programmatic config");
            Assert.AreEqual(11, config.PreferedGatewayIndex, "PreferedGatewayIndex picked up from config object");

            config.CheckGatewayProviderSettings();
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_Different_Membership_And_Reminders()
        {
            const string filename = "Config_Different_Membership_Reminders.xml";

            var config = new ClusterConfiguration();
            config.LoadFromFile(filename);
            Assert.IsTrue(config.Globals.MembershipTableAssembly == "MembershipTableDLL");
            Assert.IsTrue(config.Globals.ReminderTableAssembly == "RemindersTableDLL");
            Assert.IsTrue(config.Globals.AdoInvariant == "AdoInvariantValue");
            Assert.IsTrue(config.Globals.AdoInvariantForReminders == "AdoInvariantForReminders");
            Assert.IsTrue(config.Globals.DataConnectionString == "MembershipConnectionString");
            Assert.IsTrue(config.Globals.DataConnectionStringForReminders == "RemindersConnectionString");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void SiloConfig_Azure_SystemStore()
        {
            const string filename = "Config_NewAzure.xml";

            TraceLogger.Initialize(new NodeConfiguration());

            var siloConfig = new ClusterConfiguration();
            siloConfig.LoadFromFile(filename);

            output.WriteLine(siloConfig.Globals);

            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.AzureTable, siloConfig.Globals.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.AzureTable, siloConfig.Globals.ReminderServiceType, "ReminderServiceType");

            Assert.IsNotNull(siloConfig.Globals.DataConnectionString, "DataConnectionString should not be null");
            Assert.IsFalse(string.IsNullOrWhiteSpace(siloConfig.Globals.DataConnectionString), "DataConnectionString should not be blank");

            Assert.IsTrue(siloConfig.Globals.UseAzureSystemStore, "Should be using Azure storage");
            Assert.IsFalse(siloConfig.Globals.UseSqlSystemStore, "Should not be using SqlServer storage");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void SiloConfig_OldAzure()
        {
            const string filename = "Config_OldAzure.xml";

            TraceLogger.Initialize(new NodeConfiguration());

            var siloConfig = new ClusterConfiguration();
            siloConfig.LoadFromFile(filename);

            Assert.AreEqual(GlobalConfiguration.LivenessProviderType.AzureTable, siloConfig.Globals.LivenessType, "LivenessType");
            Assert.AreEqual(GlobalConfiguration.ReminderServiceProviderType.AzureTable, siloConfig.Globals.ReminderServiceType, "ReminderServiceType");

            Assert.IsNotNull(siloConfig.Globals.DataConnectionString, "DataConnectionString should not be null");
            Assert.IsFalse(string.IsNullOrWhiteSpace(siloConfig.Globals.DataConnectionString), "DataConnectionString should not be blank");

            Assert.IsTrue(siloConfig.Globals.UseAzureSystemStore, "Should be using Azure storage");
            Assert.IsFalse(siloConfig.Globals.UseSqlSystemStore, "Should not be using SqlServer storage");
        }

        internal static void DeleteIfExists(FileInfo fileInfo)
        {
            if (fileInfo.Exists)
            {
                fileInfo.Delete();
                fileInfo.Refresh();
            }
            Assert.IsFalse(File.Exists(fileInfo.FullName), "File.Exists: {0}", fileInfo.FullName);
            Assert.IsFalse(fileInfo.Exists, "FileInfo.Exists: {0}", fileInfo.FullName);
        }

        internal static void CreateIfNotExists(FileInfo fileInfo)
        {
            if (!File.Exists(fileInfo.FullName))
            {
                using (var stream = fileInfo.CreateText())
                {
                    stream.Flush();
                }
                fileInfo.Refresh();
            }
            Assert.IsTrue(File.Exists(fileInfo.FullName), "File.Exists: {0}", fileInfo.FullName);
            Assert.IsTrue(fileInfo.Exists, "FileInfo.Exists: {0}", fileInfo.FullName);
        }
    }

    public class DummyLogConsumer : ILogConsumer
    {
        public void Log(Severity severity, TraceLogger.LoggerType loggerType, string caller, string message, IPEndPoint myIPEndPoint, Exception exception, int eventCode = 0)
        {
            throw new NotImplementedException();
        }
    }
}

// ReSharper restore ConvertToConstant.Local
// ReSharper restore RedundantTypeArgumentsOfMethod
// ReSharper restore CheckNamespace
