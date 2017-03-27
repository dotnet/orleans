using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Orleans.TestingHost;

namespace TestExtensions
{
    public class TestDefaultConfiguration
    {
        private static object lockObject = new object();
        private static IConfiguration defaultConfiguration;

        static TestDefaultConfiguration()
        {
            InitializeDefaults();
        }

        public static void InitializeDefaults()
        {
            lock (lockObject)
            {
                TestClusterOptions.FallbackOptions.DefaultExtendedConfiguration = defaultConfiguration = BuildDefaultConfiguration();
            }
        }

        public static string DataConnectionString => defaultConfiguration[nameof(DataConnectionString)];
        public static string EventHubConnectionString => defaultConfiguration[nameof(EventHubConnectionString)];
        public static string ZooKeeperConnectionString => defaultConfiguration[nameof(ZooKeeperConnectionString)];

        private static IConfiguration BuildDefaultConfiguration()
        {
            var builder = TestClusterOptions.FallbackOptions.DefaultConfigurationBuilder();
            builder.AddInMemoryCollection(new Dictionary<string, string>
            {
                { nameof(ZooKeeperConnectionString), "127.0.0.1:2181" },
                { nameof(TestClusterOptions.FallbackOptions.TraceToConsole), "false" },
            });

            AddJsonFileInAncestorFolder(builder, "OrleansTestSecrets.json");
            builder.AddEnvironmentVariables("Orleans");

            var config = builder.Build();
            return config;
        }

        /// <summary>Try to find a file with specified name up the folder hierarchy, as some of our CI environments are configured this way.</summary>
        private static void AddJsonFileInAncestorFolder(ConfigurationBuilder builder, string fileName)
        {
            // There might be some other out-of-the-box way of doing this though.
            var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (currentDir != null && currentDir.Exists)
            {
                string filePath = Path.Combine(currentDir.FullName, fileName);
                if (File.Exists(filePath))
                {
                    builder.AddJsonFile(filePath);
                    return;
                }

                currentDir = currentDir.Parent;
            }
        }
    }
}
