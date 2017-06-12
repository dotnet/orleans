using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using Orleans;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;
using Tester;
using TestExtensions;
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
            LogManager.UnInitialize();
            this.output = output;
        }

        public void Dispose()
        {
            LogManager.UnInitialize();
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_ParseTimeSpan()
        {
            string str = "1ms";
            TimeSpan ts = TimeSpan.FromMilliseconds(1);
            Assert.Equal(ts, ConfigUtilities.ParseTimeSpan(str, str));

            str = "2s";
            ts = TimeSpan.FromSeconds(2);
            Assert.Equal(ts, ConfigUtilities.ParseTimeSpan(str, str));

            str = "3m";
            ts = TimeSpan.FromMinutes(3);
            Assert.Equal(ts, ConfigUtilities.ParseTimeSpan(str, str));

            str = "4hr";
            ts = TimeSpan.FromHours(4);
            Assert.Equal(ts, ConfigUtilities.ParseTimeSpan(str, str));

            str = "5"; // Default unit is seconds
            ts = TimeSpan.FromSeconds(5);
            Assert.Equal(ts, ConfigUtilities.ParseTimeSpan(str, str));

            str = "922337203685477.5807ms";
            ts = TimeSpan.MaxValue;
            Assert.Equal(ts, ConfigUtilities.ParseTimeSpan(str, str));
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_NewConfigTest()
        {
            TextReader input = File.OpenText("Config_TestSiloConfig.xml");
            ClusterConfiguration config = new ClusterConfiguration();
            config.Load(input);
            input.Close();

            Assert.Equal<int>(2, config.Globals.SeedNodes.Count); // Seed node count is incorrect
            Assert.Equal<IPEndPoint>(new IPEndPoint(IPAddress.Loopback, 11111), config.Globals.SeedNodes[0]); // First seed node is set incorrectly
            Assert.Equal<IPEndPoint>(new IPEndPoint(IPAddress.IPv6Loopback, 22222), config.Globals.SeedNodes[1]); // Second seed node is set incorrectly

            Assert.Equal<int>(12345, config.Defaults.Port); // Default port is set incorrectly
            Assert.Equal<string>("UnitTests.General.TestStartup,Tester", config.Defaults.StartupTypeName);

            NodeConfiguration nc;
            bool hasNodeConfig = config.TryGetNodeConfigurationForSilo("Node1", out nc);
            Assert.True(hasNodeConfig); // Node Node1 has config
            Assert.Equal<int>(11111, nc.Port); // Port is set incorrectly for node Node1
            Assert.True(nc.IsPrimaryNode, "Node1 should be primary node");
            Assert.True(nc.IsSeedNode, "Node1 should be seed node");
            Assert.False(nc.IsGatewayNode, "Node1 should not be gateway node");
            Assert.Equal<string>("UnitTests.General.TestStartup,Tester", nc.StartupTypeName); // Startup type should be copied automatically

            hasNodeConfig = config.TryGetNodeConfigurationForSilo("Node2", out nc);
            Assert.True(hasNodeConfig, "Node Node2 has config");
            Assert.Equal<int>(22222, nc.Port); // Port is set incorrectly for node Node2
            Assert.False(nc.IsPrimaryNode, "Node2 should not be primary node");
            Assert.True(nc.IsSeedNode, "Node2 should be seed node");
            Assert.True(nc.IsGatewayNode, "Node2 should be gateway node");

            hasNodeConfig = config.TryGetNodeConfigurationForSilo("Store", out nc);
            Assert.True(hasNodeConfig, "Node Store has config");
            Assert.Equal<int>(12345, nc.Port); // IP port is set incorrectly for node Store
            Assert.False(nc.IsPrimaryNode, "Store should not be primary node");
            Assert.False(nc.IsSeedNode, "Store should not be seed node");
            Assert.False(nc.IsGatewayNode, "Store should not be gateway node");

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

            //Assert.Equal<IPEndPoint>(ep, nc.Endpoint, "IP endpoint is set incorrectly for node Store");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void LogFileName()
        {
            var oc = new ClusterConfiguration();
            oc.StandardLoad();
            NodeConfiguration n = oc.CreateNodeConfigurationForSilo("Node1");
            string fname = n.TraceFileName;
            Assert.NotNull(fname);
            Assert.False(fname.Contains(":"), "Log file name should not contain colons.");

            // Check that .NET is happy with the file name
            var f = new FileInfo(fname);
            Assert.NotNull(f.Name);
            Assert.Equal(fname, f.Name);
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

            Assert.Equal(baseLogFileName, fname);

            LogManager.Initialize(n);

            Assert.True(File.Exists(baseLogFileName), "Base name log file exists: " + baseLogFileName);
            Assert.True(File.Exists(expectedLogFileName), "Expected name log file exists: " + expectedLogFileName);
            Assert.False(File.Exists(baseLogFileNamePlusOne), "Munged log file exists: " + baseLogFileNamePlusOne);
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

            Assert.Equal(baseLogFileName, fname);

            LogManager.Initialize(n);

            Assert.True(File.Exists(baseLogFileName), "Base name log file exists: " + baseLogFileName);
            Assert.True(File.Exists(expectedLogFileName), "Expected name log file exists: " + expectedLogFileName);
            Assert.False(File.Exists(baseLogFileNamePlusOne), "Munged log file exists: " + baseLogFileNamePlusOne);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void LogFile_Write_AlreadyExists()
        {
            const string siloName = "MyNode3";
            const string configFileName = "Config_NonTimestampedLogFileNames.xml";

            string logFileName = siloName + ".log";
            FileInfo fileInfo = new FileInfo(logFileName);

            CreateIfNotExists(fileInfo);
            Assert.True(fileInfo.Exists, "Log file should exist: " + fileInfo.FullName);

            long initialSize = fileInfo.Length;

            var config = new ClusterConfiguration();
            config.LoadFromFile(configFileName);
            NodeConfiguration n = config.CreateNodeConfigurationForSilo(siloName);
            string fname = n.TraceFileName;

            Assert.Equal(logFileName, fname);

            LogManager.Initialize(n);

            Assert.True(File.Exists(fileInfo.FullName), "Log file exists - before write: " + fileInfo.FullName);

            Logger myLogger = LogManager.GetLogger("MyLogger", LoggerType.Application);

            myLogger.Info("Write something");
            LogManager.Flush();

            fileInfo.Refresh(); // Need to refresh cached view of FileInfo

            Assert.True(fileInfo.Exists, "Log file exists - after write: " + fileInfo.FullName);

            long currentSize = fileInfo.Length;

            Assert.True(currentSize > initialSize, string.Format("Log file {0} should have been written to: Initial size = {1} Current size = {2}", logFileName, initialSize, currentSize));
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void LogFile_Write_NotExists()
        {
            const string siloName = "MyNode4";
            const string configFileName = "Config_NonTimestampedLogFileNames.xml";

            string logFileName = siloName + ".log";
            FileInfo fileInfo = new FileInfo(logFileName);

            DeleteIfExists(fileInfo);

            Assert.False(File.Exists(fileInfo.FullName), "Log file should not exist: " + fileInfo.FullName);

            long initialSize = 0;

            var config = new ClusterConfiguration();
            config.LoadFromFile(configFileName);
            NodeConfiguration n = config.CreateNodeConfigurationForSilo(siloName);
            string fname = n.TraceFileName;

            Assert.Equal(logFileName, fname);

            LogManager.Initialize(n);

            Assert.True(File.Exists(fileInfo.FullName), "Log file exists - before write: " + fileInfo.FullName);

            Logger myLogger = LogManager.GetLogger("MyLogger", LoggerType.Application);

            myLogger.Info("Write something");
            LogManager.Flush();

            fileInfo.Refresh(); // Need to refresh cached view of FileInfo

            Assert.True(fileInfo.Exists, "Log file exists - after write: " + fileInfo.FullName);

            long currentSize = fileInfo.Length;

            Assert.True(currentSize > initialSize, string.Format("Log file {0} should have been written to: Initial size = {1} Current size = {2}", logFileName, initialSize, currentSize));
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void LogFile_Create()
        {
            const string siloName = "MyNode5";

            string logFileName = siloName + ".log";
            FileInfo fileInfo = new FileInfo(logFileName);

            DeleteIfExists(fileInfo);

            bool fileExists = fileInfo.Exists;
            Assert.False(fileExists, "Log file should not exist: " + fileInfo.FullName);

            CreateIfNotExists(fileInfo);

            fileExists = fileInfo.Exists;
            Assert.True(fileExists, "Log file should exist: " + fileInfo.FullName);

            long initialSize = fileInfo.Length;
            bool isLogFileEmpty = initialSize == 0;
            Assert.True(isLogFileEmpty, $"Log file {logFileName} should be empty. Current size = {initialSize}");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ClientConfig_Default_ToString()
        {
            var cfg = new ClientConfiguration();
            var str = cfg.ToString();
            Assert.NotNull(str);
            output.WriteLine(str);
            Assert.Null(cfg.SourceFile);
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
            Assert.Null(cfg.TraceFileName);

            cfg.TraceFilePattern = null;
            output.WriteLine(cfg.ToString());
            Assert.Null(cfg.TraceFileName);
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
            Assert.Null(cfg.TraceFileName); // TraceFileName should be null

            cfg.TraceFilePattern = null;
            output.WriteLine(cfg.ToString());
            Assert.Null(cfg.TraceFileName); // TraceFileName should be null
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Logger")]
        public void ClientConfig_LogConsumers()
        {
            LogManager.UnInitialize();

            string filename = "Config_LogConsumers-ClientConfiguration.xml";

            var cfg = ClientConfiguration.LoadFromFile(filename);
            Assert.Equal(filename, cfg.SourceFile);

            LogManager.Initialize(cfg);
            Assert.Equal(1, LogManager.LogConsumers.Count);
            Assert.Equal(typeof(DummyLogConsumer).FullName, LogManager.LogConsumers.Last().GetType().FullName); // Log consumer type #1

            Assert.Equal(1, LogManager.TelemetryConsumers.Count);
            Assert.Equal(typeof(TraceTelemetryConsumer).FullName, LogManager.TelemetryConsumers.First().GetType().FullName); // TelemetryConsumers consumer type #1
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Logger")]
        public void ServerConfig_LogConsumers()
        {
            LogManager.UnInitialize();

            string filename = "Config_LogConsumers-OrleansConfiguration.xml";

            var cfg = new ClusterConfiguration();
            cfg.LoadFromFile(filename);
            Assert.Equal(filename, cfg.SourceFile);

            LogManager.Initialize(cfg.CreateNodeConfigurationForSilo("Primary"));

            var actualLogConsumers = LogManager.LogConsumers.Select(x => x.GetType()).ToList();
            Assert.Contains(typeof(DummyLogConsumer), actualLogConsumers);
            Assert.Equal(1, actualLogConsumers.Count);

            var actualTelemetryConsumers = LogManager.TelemetryConsumers.Select(x => x.GetType()).ToList();
            Assert.Contains(typeof(TraceTelemetryConsumer), actualTelemetryConsumers);
            Assert.Contains(typeof(ConsoleTelemetryConsumer), actualTelemetryConsumers);
            Assert.Equal(2, actualTelemetryConsumers.Count);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Limits")]
        public void Limits_ClientConfig()
        {
            const string filename = "Config_LogConsumers-ClientConfiguration.xml";
            var config = ClientConfiguration.LoadFromFile(filename);

            string limitName;
            LimitValue limit;
            //Assert.True(config.LimitManager.LimitValues.Count >= 3, "Number of LimitValues: " + string.Join(",", config.LimitValues));
            for (int i = 1; i <= 3; i++)
            {
                limitName = "Limit" + i;
                limit = config.LimitManager.GetLimit(limitName);
                Assert.NotNull(limit);
                Assert.Equal(limitName, limit.Name); // Limit name " + i
                Assert.Equal(i, limit.SoftLimitThreshold); // Soft limit " + i
                Assert.Equal(2 * i, limit.HardLimitThreshold); // Hard limit " + i
            }

            limitName = "NoHardLimit";
            limit = config.LimitManager.GetLimit(limitName);
            Assert.NotNull(limit);
            Assert.Equal(limitName, limit.Name); // Limit name " + limitName
            Assert.Equal(4, limit.SoftLimitThreshold); // Soft limit " + limitName
            Assert.Equal(0, limit.HardLimitThreshold); // Hard limit " + limitName
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Limits")]
        public void Limits_ServerConfig()
        {
            const string filename = "Config_LogConsumers-OrleansConfiguration.xml";
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            NodeConfiguration config;
            bool hasNodeConfig = orleansConfig.TryGetNodeConfigurationForSilo("Primary", out config);
            Assert.True(hasNodeConfig); // Node Primary has config

            string limitName;
            LimitValue limit;
            //Assert.True(config.LimitManager.LimitValues.Count >= 3, "Number of LimitValues: " + string.Join(",", config.LimitValues));
            for (int i = 1; i <= 3; i++)
            {
                limitName = "Limit" + i;
                limit = config.LimitManager.GetLimit(limitName);
                Assert.NotNull(limit);
                Assert.Equal(limitName, limit.Name); // Limit name " + i
                Assert.Equal(i, limit.SoftLimitThreshold); // Soft limit " + i
                Assert.Equal(2 * i, limit.HardLimitThreshold); // Hard limit " + i
            }

            limitName = "NoHardLimit";
            limit = config.LimitManager.GetLimit(limitName);
            Assert.NotNull(limit);
            Assert.Equal(limitName, limit.Name); // Limit name " + limitName
            Assert.Equal(4, limit.SoftLimitThreshold); // Soft limit " + limitName
            Assert.Equal(0, limit.HardLimitThreshold); // Hard limit " + limitName
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Limits")]
        public void Limits_ClientConfig_NotSpecified()
        {
            const string filename = "Config_LogConsumers-ClientConfiguration.xml";
            var config = ClientConfiguration.LoadFromFile(filename);

            string limitName = "NotPresent";
            LimitValue limit = config.LimitManager.GetLimit(limitName);
            Assert.Equal(0, limit.SoftLimitThreshold);
            Assert.Equal(0, limit.HardLimitThreshold);
            Assert.Equal(limitName, limit.Name);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Limits")]
        public void Limits_ServerConfig_NotSpecified()
        {
            const string filename = "Config_LogConsumers-OrleansConfiguration.xml";
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            NodeConfiguration config;
            bool hasNodeConfig = orleansConfig.TryGetNodeConfigurationForSilo("Primary", out config);
            Assert.True(hasNodeConfig, "Node Primary has config");

            string limitName = "NotPresent";
            LimitValue limit = config.LimitManager.GetLimit(limitName);
            Assert.Equal(0, limit.SoftLimitThreshold);
            Assert.Equal(0, limit.HardLimitThreshold);
            Assert.Equal(limitName, limit.Name);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Limits")]
        public void Limits_LimitsManager_ServerConfig()
        {
            const string filename = "Config_LogConsumers-OrleansConfiguration.xml";
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            NodeConfiguration config;
            bool hasNodeConfig = orleansConfig.TryGetNodeConfigurationForSilo("Primary", out config);
            Assert.True(hasNodeConfig, "Node Primary has config");

            string limitName;
            LimitValue limit;
            for (int i = 1; i <= 3; i++)
            {
                limitName = "Limit" + i;
                limit = config.LimitManager.GetLimit(limitName);
                Assert.NotNull(limit);
                Assert.Equal(limitName, limit.Name); // Limit name " + i
                Assert.Equal(i, limit.SoftLimitThreshold); // Soft limit " + i
                Assert.Equal(2 * i, limit.HardLimitThreshold); // Hard limit " + i
            }

            limitName = "NoHardLimit";
            limit = config.LimitManager.GetLimit(limitName);
            Assert.NotNull(limit);
            Assert.Equal(limitName, limit.Name); // Limit name " + limitName
            Assert.Equal(4, limit.SoftLimitThreshold); // Soft limit " + limitName
            Assert.Equal(0, limit.HardLimitThreshold); // No Hard limit " + limitName

            limitName = "NotPresent";
            limit = config.LimitManager.GetLimit(limitName);
            Assert.NotNull(limit);
            Assert.Equal(limitName, limit.Name); // Limit name " + limitName
            Assert.Equal(0, limit.SoftLimitThreshold); // No Soft limit " + limitName
            Assert.Equal(0, limit.HardLimitThreshold); // No Hard limit " + limitName
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
                Assert.NotNull(limit);
                Assert.Equal(limitName, limit.Name); // Limit name " + i
                Assert.Equal(i, limit.SoftLimitThreshold); // Soft limit " + i
                Assert.Equal(2 * i, limit.HardLimitThreshold); // Hard limit " + i
            }

            limitName = "NoHardLimit";
            limit = config.LimitManager.GetLimit(limitName);
            Assert.NotNull(limit);
            Assert.Equal(limitName, limit.Name); // Limit name " + limitName
            Assert.Equal(4, limit.SoftLimitThreshold); // Soft limit " + limitName
            Assert.Equal(0, limit.HardLimitThreshold); // No Hard limit " + limitName

            limitName = "NotPresent";
            limit = config.LimitManager.GetLimit(limitName);
            Assert.NotNull(limit);
            Assert.Equal(limitName, limit.Name); // Limit name " + limitName
            Assert.Equal(0, limit.SoftLimitThreshold); // No Soft limit " + limitName
            Assert.Equal(0, limit.HardLimitThreshold); // No Hard limit " + limitName
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
                Assert.NotNull(limit);
                Assert.Equal(limitName, limit.Name); // Limit name " + i
                Assert.Equal(i, limit.SoftLimitThreshold); // Soft limit " + i
                Assert.Equal(0, limit.HardLimitThreshold); // No Hard limit " + i
            }
            for (int i = 1; i <= 3; i++)
            {
                limitName = "NotPresent" + i;
                limit = config.LimitManager.GetLimit(limitName, i, 2 * i);
                Assert.NotNull(limit);
                Assert.Equal(limitName, limit.Name); // Limit name " + i
                Assert.Equal(i, limit.SoftLimitThreshold); // Soft limit " + i
                Assert.Equal(2 * i, limit.HardLimitThreshold); // Hard limit " + i
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
            Assert.True(hasNodeConfig); // Node Primary has config"

            string limitName;
            LimitValue limit;
            for (int i = 1; i <= 3; i++)
            {
                limitName = "NotPresent" + i;
                limit = config.LimitManager.GetLimit(limitName, i);
                Assert.NotNull(limit);
                Assert.Equal(limitName, limit.Name); // Limit name " + i
                Assert.Equal(i, limit.SoftLimitThreshold); // Soft limit " + i
                Assert.Equal(0, limit.HardLimitThreshold); // No Hard limit " + i
            }
            for (int i = 1; i <= 3; i++)
            {
                limitName = "NotPresent" + i;
                limit = config.LimitManager.GetLimit(limitName, i, 2 * i);
                Assert.NotNull(limit);
                Assert.Equal(limitName, limit.Name); // Limit name " + i
                Assert.Equal(i, limit.SoftLimitThreshold); // Soft limit " + i
                Assert.Equal(2 * i, limit.HardLimitThreshold); // Hard limit " + i
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void Config_AzureConnectionInfo()
        {
            string azureConnectionStringInput =
                @"DefaultEndpointsProtocol=https;AccountName=test;AccountKey=q-SOMEKEY-==";
            output.WriteLine("Input = " + azureConnectionStringInput);
            string azureConnectionString = ConfigUtilities.RedactConnectionStringInfo(azureConnectionStringInput);
            output.WriteLine("Output = " + azureConnectionString);
            Assert.True(azureConnectionString.EndsWith("AccountKey=<--SNIP-->", StringComparison.InvariantCultureIgnoreCase),
                "Removed account key info from Azure connection string " + azureConnectionString);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("SqlServer")]
        public void Config_SqlConnectionInfo()
        {
            string sqlConnectionStringInput =
                @"Server=myServerName\myInstanceName;Database=myDataBase;User Id=myUsername;Password=myPassword";
            output.WriteLine("Input = " + sqlConnectionStringInput);
            string sqlConnectionString = ConfigUtilities.RedactConnectionStringInfo(sqlConnectionStringInput);
            output.WriteLine("Output = " + sqlConnectionString);
            Assert.True(sqlConnectionString.EndsWith("Password=<--SNIP-->", StringComparison.InvariantCultureIgnoreCase),
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
            ValidateProviderConfigs(providerConfigs, numProviders);

            ProviderConfiguration pCfg = (ProviderConfiguration)providerConfigs.Providers.Values.ToList()[0];
            Assert.Equal("orleanstest1", pCfg.Name); // Provider name #1
            Assert.Equal("AzureTable", pCfg.Type); // Provider type #1
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void Config_StorageProvider_Azure2()
        {
            const string filename = "Config_StorageProvider_Azure2.xml";
            const int numProviders = 2;
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            var providerConfigs = orleansConfig.Globals.ProviderConfigurations["Storage"];
            ValidateProviderConfigs(providerConfigs, numProviders);

            ProviderConfiguration pCfg = (ProviderConfiguration)providerConfigs.Providers.Values.ToList()[0];
            Assert.Equal("orleanstest1", pCfg.Name); // Provider name #1
            Assert.Equal("AzureTable", pCfg.Type); // Provider type #1

            pCfg = (ProviderConfiguration)providerConfigs.Providers.Values.ToList()[1];
            Assert.Equal("orleanstest2", pCfg.Name); // Provider name #2
            Assert.Equal("AzureTable", pCfg.Type); // Provider type #2
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Providers")]
        public void Config_StorageProvider_NoConfig()
        {
            const string filename = "Config_StorageProvider_Memory2.xml";
            const int numProviders = 2;
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            var providerConfigs = orleansConfig.Globals.ProviderConfigurations["Storage"];
            ValidateProviderConfigs(providerConfigs, numProviders);
            for (int i = 0; i < providerConfigs.Providers.Count; i++)
            {
                IProviderConfiguration provider = providerConfigs.Providers.Values.ToList()[i];
                Assert.Equal("test" + i, ((ProviderConfiguration)provider).Name); // Provider name #" + i
                Assert.Equal(typeof(MockStorageProvider).FullName, ((ProviderConfiguration)provider).Type); // Provider type #" + i
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
            ValidateProviderConfigs(providerConfigs, numProviders);

            for (int i = 0; i < providerConfigs.Providers.Count; i++)
            {
                IProviderConfiguration provider = providerConfigs.Providers.Values.ToList()[i];
                Assert.Equal("config" + i, ((ProviderConfiguration)provider).Name); // Provider name #" + i
                Assert.Equal(typeof(MockStorageProvider).FullName, ((ProviderConfiguration)provider).Type); // Provider type #" + i
                for (int j = 0; j < 2; j++)
                {
                    int num = 2 * i + j;
                    string key = "Prop" + num;
                    string cfg = provider.Properties[key];
                    Assert.NotNull(cfg); // Null config value " + key
                    Assert.False(string.IsNullOrWhiteSpace(cfg)); // Blank config value " + key
                    Assert.Equal(num.ToString(CultureInfo.InvariantCulture), string.Format(cfg, "Config value {0} = {1}", key, cfg));
                }
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Providers")]
        public void Config_BootstrapProviders()
        {
            const string filename = "Config_BootstrapProviders.xml";
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);
            var providerConfigs = orleansConfig.Globals.ProviderConfigurations["Bootstrap"];
            ValidateProviderConfigs(providerConfigs, 2);

            var providers = providerConfigs.Providers.Values.Cast<ProviderConfiguration>().ToList();
            Assert.Equal("bootstrap1", providers[0].Name);
            Assert.Equal("UnitTests.General.MockBootstrapProvider", providers[0].Type);
            Assert.Equal("bootstrap2", providers[1].Name);
            Assert.Equal("UnitTests.General.GrainCallBootstrapper", providers[1].Type);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_AdditionalAssemblyPaths_Config()
        {
            const string filename = "Config_AdditionalAssemblies.xml";
            const int numPaths = 2;
            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);

            Assert.NotNull(orleansConfig.Defaults.AdditionalAssemblyDirectories); // Additional Assembly Dictionary
            Assert.Equal(numPaths, orleansConfig.Defaults.AdditionalAssemblyDirectories.Count); // Additional Assembly count
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void Config_StorageProviders_AzureTable_Default()
        {
            const string filename = "Config_StorageProvider_Azure1.xml";

            var config = new ClusterConfiguration();
            config.LoadFromFile(filename);

            output.WriteLine(config.Globals.ToString());

            Assert.Equal(GlobalConfiguration.LivenessProviderType.MembershipTableGrain, config.Globals.LivenessType); // LivenessType
            Assert.Equal(GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain, config.Globals.ReminderServiceType); // ReminderServiceType
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Gateway")]
        public void ClientConfig_Default()
        {
            const string filename = "ClientConfiguration.xml";

            ClientConfiguration config = ClientConfiguration.LoadFromFile(filename);

            output.WriteLine(config);

            Assert.Equal(ClientConfiguration.GatewayProviderType.Config, config.GatewayProvider); // GatewayProviderType
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void ClientConfig_ClientInit_FromFile()
        {
            const string filename = "ClientConfig_NewAzure.xml";

            var client = new ClientBuilder().LoadConfiguration(filename).Build();
            try
            {
                ClientConfiguration config = client.Configuration;

                output.WriteLine(config);

                Assert.NotNull(config); // Client.CurrentConfig

                Assert.Equal(filename, Path.GetFileName(config.SourceFile)); // ClientConfig.SourceFile

                // GatewayProviderType
                Assert.Equal(ClientConfiguration.GatewayProviderType.AzureTable, config.GatewayProvider);
            }
            finally
            {
                client.Dispose();
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void ClientConfig_AzureInit_FileNotFound()
        {
            const string filename = "ClientConfig_NotFound.xml";
            Assert.Throws<FileNotFoundException>(() => new ClientBuilder().LoadConfiguration(filename).Build());
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void ClientConfig_FromFile_FileNotFound()
        {
            const string filename = "ClientConfig_NotFound.xml";
            Assert.Throws<FileNotFoundException>(() =>
            ClientConfiguration.LoadFromFile(filename));
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void ServerConfig_FromFile_FileNotFound()
        {
            const string filename = "SiloConfig_NotFound.xml";
            var config = new ClusterConfiguration();
            Assert.Throws<FileNotFoundException>(() =>
                config.LoadFromFile(filename));
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ClientConfig_LoadFrom()
        {
            string filename = "Config_LogConsumers-ClientConfiguration.xml";

            var config = ClientConfiguration.LoadFromFile(filename);

            Assert.NotNull(config); // ClientConfiguration null
            Assert.NotNull(config.ToString()); // ClientConfiguration.ToString

            Assert.Equal(filename, Path.GetFileName(config.SourceFile)); // ClientConfig.SourceFile
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ServerConfig_LoadFrom()
        {
            string filename = "Config_LogConsumers-OrleansConfiguration.xml";

            var config = new ClusterConfiguration();
            config.LoadFromFile(filename);

            Assert.NotNull(config.ToString()); // OrleansConfiguration.ToString

            Assert.Equal(filename, Path.GetFileName(config.SourceFile)); // OrleansConfiguration.SourceFile
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("SqlServer")]
        public void ClientConfig_SqlServer()
        {
            const string filename = "DevTestClientConfiguration.xml";

            ClientConfiguration config = ClientConfiguration.LoadFromFile(filename);

            output.WriteLine(config);

            Assert.Equal(ClientConfiguration.GatewayProviderType.SqlServer, config.GatewayProvider); // GatewayProviderType
            Assert.Equal(ClientConfiguration.GatewayProviderType.SqlServer, config.GatewayProviderToUse); // GatewayProviderToUse

            Assert.NotNull(config.DataConnectionString); // Connection string should not be null
            Assert.False(string.IsNullOrWhiteSpace(config.DataConnectionString)); // Connection string should not be blank

            Assert.False(config.UseAzureSystemStore); // Should not be using Azure storage
            Assert.True(config.UseSqlSystemStore); // Should be using SqlServer storage
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("SqlServer")]
        public void ClientConfig_SqlServer_StatsProvider()
        {
            const string filename = "DevTestClientConfiguration.xml";

            ClientConfiguration config = ClientConfiguration.LoadFromFile(filename);

            output.WriteLine(config);

            Assert.Equal(1, config.ProviderConfigurations.Count); // Number of Providers Types
            Assert.Equal("Statistics", config.ProviderConfigurations.Keys.First()); // Client Stats Providers
            ProviderCategoryConfiguration statsProviders = config.ProviderConfigurations["Statistics"];
            Assert.Equal(1, statsProviders.Providers.Count); // Number of Stats Providers
            Assert.Equal("SQL", statsProviders.Providers.Keys.First()); // Stats provider name
            ProviderConfiguration providerConfig = (ProviderConfiguration)statsProviders.Providers["SQL"];
            // Note: Use string here instead of typeof(SqlStatisticsPublisher).FullName to prevent cascade load of this type
            Assert.Equal("Orleans.Providers.SqlServer.SqlStatisticsPublisher", providerConfig.Type); // Stats provider class name
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("SqlServer")]
        public void SiloConfig_SqlServer()
        {
            const string filename = "DevTestServerConfiguration.xml";
            Guid myGuid = Guid.Empty;

            LogManager.Initialize(new NodeConfiguration());

            var orleansConfig = new ClusterConfiguration();
            orleansConfig.LoadFromFile(filename);

            output.WriteLine(orleansConfig.Globals);

            Assert.Equal(GlobalConfiguration.LivenessProviderType.SqlServer, orleansConfig.Globals.LivenessType); // LivenessType
            Assert.Equal(GlobalConfiguration.ReminderServiceProviderType.SqlServer, orleansConfig.Globals.ReminderServiceType); // ReminderServiceType

            Assert.NotNull(orleansConfig.Globals.DataConnectionString); // DataConnectionString should not be null
            Assert.False(string.IsNullOrWhiteSpace(orleansConfig.Globals.DataConnectionString)); // DataConnectionString should not be blank

            Assert.False(orleansConfig.Globals.UseAzureSystemStore); // Should not be using Azure storage
            Assert.True(orleansConfig.Globals.UseSqlSystemStore); // Should be using SqlServer storage

            Assert.Equal(orleansConfig.Globals.ServiceId, myGuid); // ServiceId
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("SqlServer")]
        public void SiloConfig_SqlServer_StatsProvider()
        {
            const string filename = "DevTestServerConfiguration.xml";

            var config = new ClusterConfiguration();
            config.LoadFromFile(filename);

            output.WriteLine(config);

            Assert.Equal(2, config.Globals.ProviderConfigurations.Count); // Number of Providers Types
            Assert.True(config.Globals.ProviderConfigurations.Keys.Contains("Statistics")); // Stats Providers
            ProviderCategoryConfiguration statsProviders = config.Globals.ProviderConfigurations["Statistics"];
            Assert.Equal(1, statsProviders.Providers.Count); // Number of Stats Providers
            Assert.Equal("SQL", statsProviders.Providers.Keys.First()); // Stats provider name
            ProviderConfiguration providerConfig = (ProviderConfiguration)statsProviders.Providers["SQL"];
            // Note: Use string here instead of typeof(SqlStatisticsPublisher).FullName to prevent cascade load of this type
            Assert.Equal("Orleans.Providers.SqlServer.SqlStatisticsPublisher", providerConfig.Type); // Stats provider class name
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void SiloConfig_Azure_Default()
        {
            const string filename = "Config_Azure_Default.xml";

            string deploymentId = "SiloConfig_Azure_Default" + TestConstants.random.Next();
            string connectionString = "UseDevelopmentStorage=true";

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

            Assert.Equal(GlobalConfiguration.LivenessProviderType.AzureTable, siloConfig.Globals.LivenessType); // LivenessType
            Assert.Equal(GlobalConfiguration.ReminderServiceProviderType.AzureTable, siloConfig.Globals.ReminderServiceType); // ReminderServiceType

            Assert.Equal(deploymentId, siloConfig.Globals.DeploymentId); // DeploymentId
            Assert.Equal(connectionString, siloConfig.Globals.DataConnectionString); // DataConnectionString

            Assert.True(siloConfig.Globals.UseAzureSystemStore, "Should be using Azure storage");
            Assert.False(siloConfig.Globals.UseSqlSystemStore, "Should not be using SqlServer storage");
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

            Assert.Equal("", siloConfig.SourceFile); // SourceFile should be blank for programmatic config
            Assert.Equal(11, siloConfig.Globals.CacheSize); // CacheSize picked up from config object
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void ClientConfig_Programatic()
        {
            string deploymentId = "ClientConfig_Programatic";

            var config = new ClientConfiguration();

            config.DeploymentId = deploymentId;
            config.DataConnectionString = "UseDevelopmentStorage=true";
            config.GatewayProvider = ClientConfiguration.GatewayProviderType.AzureTable;

            config.PreferedGatewayIndex = 11;

            output.WriteLine(config);

            Assert.Equal(null, config.SourceFile); // SourceFile should be blank for programmatic config
            Assert.Equal(11, config.PreferedGatewayIndex); // PreferedGatewayIndex picked up from config object

            config.CheckGatewayProviderSettings();
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_Custom_Membership_No_Reminders()
        {
            const string filename = "Config_Custom_Membership_No_Reminders.xml";

            var config = new ClusterConfiguration();
            config.LoadFromFile(filename);
            Assert.True(config.Globals.MembershipTableAssembly == "MembershipTableDLL");
            Assert.True(config.Globals.ReminderServiceType == GlobalConfiguration.ReminderServiceProviderType.Disabled);
            Assert.True(config.Globals.AdoInvariant == "AdoInvariantValue");
            Assert.True(config.Globals.AdoInvariantForReminders == "AdoInvariantForReminders");
            Assert.True(config.Globals.DataConnectionString == "MembershipConnectionString");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_Different_Membership_And_Reminders()
        {
            const string filename = "Config_Different_Membership_Reminders.xml";

            var config = new ClusterConfiguration();
            config.LoadFromFile(filename);
            Assert.True(config.Globals.MembershipTableAssembly == "MembershipTableDLL");
            Assert.True(config.Globals.ReminderTableAssembly == "RemindersTableDLL");
            Assert.True(config.Globals.AdoInvariant == "AdoInvariantValue");
            Assert.True(config.Globals.AdoInvariantForReminders == "AdoInvariantForReminders");
            Assert.True(config.Globals.DataConnectionString == "MembershipConnectionString");
            Assert.True(config.Globals.DataConnectionStringForReminders == "RemindersConnectionString");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config")]
        public void Config_TableGrain()
        {
            const string filename = "Config_TableGrain.xml";

            var config = new ClusterConfiguration();
            config.LoadFromFile(filename);
            Assert.True(config.Globals.ReminderServiceType == GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain);
            Assert.True(config.Globals.LivenessType == GlobalConfiguration.LivenessProviderType.MembershipTableGrain);
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void SiloConfig_Azure_SystemStore()
        {
            const string filename = "Config_NewAzure.xml";

            LogManager.Initialize(new NodeConfiguration());

            var siloConfig = new ClusterConfiguration();
            siloConfig.LoadFromFile(filename);

            output.WriteLine(siloConfig.Globals);

            Assert.Equal(GlobalConfiguration.LivenessProviderType.AzureTable, siloConfig.Globals.LivenessType); // LivenessType
            Assert.Equal(GlobalConfiguration.ReminderServiceProviderType.AzureTable, siloConfig.Globals.ReminderServiceType); // ReminderServiceType

            Assert.NotNull(siloConfig.Globals.DataConnectionString); // DataConnectionString should not be null
            Assert.False(string.IsNullOrWhiteSpace(siloConfig.Globals.DataConnectionString)); // DataConnectionString should not be blank

            Assert.True(siloConfig.Globals.UseAzureSystemStore, "Should be using Azure storage");
            Assert.False(siloConfig.Globals.UseSqlSystemStore, "Should not be using SqlServer storage");
        }

        [Fact, TestCategory("Functional"), TestCategory("Config"), TestCategory("Azure")]
        public void SiloConfig_OldAzure()
        {
            const string filename = "Config_OldAzure.xml";

            LogManager.Initialize(new NodeConfiguration());

            var siloConfig = new ClusterConfiguration();
            siloConfig.LoadFromFile(filename);

            Assert.Equal(GlobalConfiguration.LivenessProviderType.AzureTable, siloConfig.Globals.LivenessType); // LivenessType
            Assert.Equal(GlobalConfiguration.ReminderServiceProviderType.AzureTable, siloConfig.Globals.ReminderServiceType); // ReminderServiceType

            Assert.NotNull(siloConfig.Globals.DataConnectionString); // DataConnectionString should not be null
            Assert.False(string.IsNullOrWhiteSpace(siloConfig.Globals.DataConnectionString), "DataConnectionString should not be blank");

            Assert.True(siloConfig.Globals.UseAzureSystemStore, "Should be using Azure storage");
            Assert.False(siloConfig.Globals.UseSqlSystemStore, "Should not be using SqlServer storage");
        }

        internal static void DeleteIfExists(FileInfo fileInfo)
        {
            if (fileInfo.Exists)
            {
                fileInfo.Delete();
                fileInfo.Refresh();
            }
            Assert.False(File.Exists(fileInfo.FullName), $"File.Exists: {fileInfo.FullName}");
            Assert.False(fileInfo.Exists, $"FileInfo.Exists: {fileInfo.FullName}");
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
            Assert.True(File.Exists(fileInfo.FullName), $"File.Exists: {fileInfo.FullName}");
            Assert.True(fileInfo.Exists, $"FileInfo.Exists: {fileInfo.FullName}");
        }

        private static void ValidateProviderConfigs(ProviderCategoryConfiguration providerConfigs, int numProviders)
        {
            Assert.NotNull(providerConfigs); // Null provider configs
            Assert.NotNull(providerConfigs.Providers); // Null providers
            Assert.Equal(numProviders, providerConfigs.Providers.Count); // Num provider configs
        }
    }

    public class DummyLogConsumer : ILogConsumer
    {
        public void Log(Severity severity, LoggerType loggerType, string caller, string message, IPEndPoint myIPEndPoint, Exception exception, int eventCode = 0)
        {
            throw new NotImplementedException();
        }
    }
}

// ReSharper restore ConvertToConstant.Local
// ReSharper restore RedundantTypeArgumentsOfMethod
// ReSharper restore CheckNamespace