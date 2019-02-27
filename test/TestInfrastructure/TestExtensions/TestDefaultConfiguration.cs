using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Orleans.TestingHost;
using Orleans.TestingHost.Extensions;

namespace TestExtensions
{
    public class TestDefaultConfiguration
    {
        private static readonly object LockObject = new object();
        private static IConfiguration defaultConfiguration;

        static TestDefaultConfiguration()
        {
            InitializeDefaults();
        }

        public static void InitializeDefaults()
        {
            lock (LockObject)
            {
                defaultConfiguration = BuildDefaultConfiguration();
            }
        }

        public static string DataConnectionString => defaultConfiguration[nameof(DataConnectionString)];
        public static string EventHubConnectionString => defaultConfiguration[nameof(EventHubConnectionString)];
        public static string ZooKeeperConnectionString => defaultConfiguration[nameof(ZooKeeperConnectionString)];

        private static IConfiguration BuildDefaultConfiguration()
        {
            var builder = new ConfigurationBuilder();
            ConfigureHostConfiguration(builder);

            var config = builder.Build();
            return config;
        }

        public static void ConfigureHostConfiguration(IConfigurationBuilder builder)
        {
            builder.AddInMemoryCollection(new Dictionary<string, string>
            {
                { nameof(ZooKeeperConnectionString), "127.0.0.1:2181" }
            });

            AddJsonFileInAncestorFolder(builder, "OrleansTestSecrets.json");
            builder.AddEnvironmentVariables("Orleans");
        }

        /// <summary>
        /// Hack, allowing PhysicalFileProvider to be serialized using json
        /// </summary>
        [Serializable]
        [JsonObject(MemberSerialization.OptIn)]
        private class MyPhysicalFileProvider : IFileProvider
        {
            private PhysicalFileProvider fileProvider;
            
            [JsonProperty]
            public string Root { get; set; }

            public IDirectoryContents GetDirectoryContents(string subpath)
            {
                return this.FileProvider().GetDirectoryContents(subpath);
            }

            public IFileInfo GetFileInfo(string subpath)
            {
                return this.FileProvider().GetFileInfo(subpath);
            }

            public IChangeToken Watch(string filter)
            {
                return this.FileProvider().Watch(filter);
            }

            private PhysicalFileProvider FileProvider()
            {
                return fileProvider ?? (fileProvider = new PhysicalFileProvider(this.Root));
            }
        }

        /// <summary>Try to find a file with specified name up the folder hierarchy, as some of our CI environments are configured this way.</summary>
        private static void AddJsonFileInAncestorFolder(IConfigurationBuilder builder, string fileName)
        {
            // There might be some other out-of-the-box way of doing this though.
            var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (currentDir != null && currentDir.Exists)
            {
                string filePath = Path.Combine(currentDir.FullName, fileName);
                if (File.Exists(filePath))
                {
                    builder.AddJsonFile(new MyPhysicalFileProvider { Root = currentDir.FullName }, fileName, false, false);
                    return;
                }

                currentDir = currentDir.Parent;
            }
        }

        public static void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.ConfigureBuilder(() =>
            {
                if (builder.Properties.TryGetValue(nameof(LegacyTestClusterConfiguration), out var legacyConfigObj) &&
                    legacyConfigObj is LegacyTestClusterConfiguration legacyConfig)
                {
                    legacyConfig.ClusterConfiguration.AdjustForTestEnvironment(DataConnectionString);
                    legacyConfig.ClientConfiguration.AdjustForTestEnvironment(DataConnectionString);
                }
            });
            builder.ConfigureHostConfiguration(ConfigureHostConfiguration);
        }
    }
}
