using Newtonsoft.Json.Linq;
using Orleans.Tests.SqlUtils;
using Orleans.TestingHost.Utils;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using UnitTests.General;
using TestExtensions;

namespace UnitTests.StorageTests.Relational
{
    [Serializable]
    [DebuggerDisplay("StorageInvariant = {StorageInvariant}, ConnectionString = {ConnectionString}")]
    [Orleans.GenerateSerializer]
    public struct StorageConnection
    {
        [Orleans.Id(0)]
        public string StorageInvariant { get; set; }

        [Orleans.Id(1)]
        public string ConnectionString { get; set; }
    }

    [Serializable]
    [Orleans.GenerateSerializer]
    public class TestEnvironmentSettings
    {
        [Orleans.Id(0)]
        public ICollection<StorageConnection> ConnectionStrings { get; set; }

        [Orleans.Id(1)]
        public string EnvironmentId { get; set; }
    }

    /// <summary>
    /// This enforces the necessary environment invariants hold before starting to run tests.
    /// This servers as a class or object invariant for the test environment.
    /// </summary>
    public class TestEnvironmentInvariant
    {
        /// <summary>
        /// An environment variable to set a test settings file location.
        /// </summary>
        public const string EnvVariableForCustomSettingLocation = "customTestSettingsFileLocation";

        /// <summary>
        /// A default custom settings file location if <see cref="EnvVariableForCustomSettingLocation"/> is not set.
        /// </summary>
        public const string FallBackCustomTestSettingsFileLocation = @"..\..\..\CustomTestSettings.json";

        /// <summary>
        /// The default test settings before merging with external ones.
        /// </summary>
        public TestEnvironmentSettings DefaultSettings { get; } = new TestEnvironmentSettings
        {
            ConnectionStrings = new Collection<StorageConnection>(new List<StorageConnection>(new[]
            {
                new StorageConnection
                {
                    StorageInvariant = AdoNetInvariants.InvariantNameSqlServer,
                    ConnectionString = TestDefaultConfiguration.MsSqlConnectionString
                },
                new StorageConnection
                {
                    StorageInvariant = AdoNetInvariants.InvariantNameMySql,
                    ConnectionString = TestDefaultConfiguration.MySqlConnectionString
                },
                new StorageConnection
                {
                    StorageInvariant = AdoNetInvariants.InvariantNamePostgreSql,
                    ConnectionString = TestDefaultConfiguration.PostgresConnectionString
                }
            })),
            EnvironmentId = "Default"
        };

        /// <summary>
        /// The active settings after merging the default ones with the active ones.
        /// </summary>
        public TestEnvironmentSettings ActiveSettings { get; set; }

        /// <summary>
        /// The default constructor.
        /// </summary>
        public TestEnvironmentInvariant()
        {
            ActiveSettings = TryLoadAndMergeWithCustomSettings(DefaultSettings);
        }

        /// <summary>
        /// Ensures the storage with the given connection is functional and if not, tries to make it functional.
        /// </summary>
        /// <param name="connection">The connection with which to ensure the storage is functional.</param>
        /// <param name="storageName">Storage name. This is optional.</param>
        /// <returns></returns>
        public RelationalStorageForTesting EnsureStorageForTesting(StorageConnection connection, string storageName = null)
        {

            if (AdoNetInvariants.Invariants.Contains(connection.StorageInvariant))
            {
                const string RelationalStorageTestDb = "OrleansStorageTests";
                return RelationalStorageForTesting.SetupInstance(connection.StorageInvariant, storageName ?? RelationalStorageTestDb, connection.ConnectionString).GetAwaiter().GetResult();
            }

            return null;
        }

        /// <summary>
        /// Tries to ensure the storage emulator is running before the tests start.
        /// </summary>
        /// <remarks>This could perhaps have more functionality.</remarks>
        public bool EnsureEmulatorStorageForTesting()
        {
            return StorageEmulator.TryStart();
        }

        /// <summary>
        /// Tries to load custom settings and if one is find, tries to merge them to the given default settings.
        /// </summary>
        /// <param name="defaultSettings">The default settings with which to merge the ones.</param>
        /// <returns>The result settings after merge.</returns>
        private static TestEnvironmentSettings TryLoadAndMergeWithCustomSettings(TestEnvironmentSettings defaultSettings)
        {
            string customTestSettingsFileLocation = Environment.GetEnvironmentVariable(EnvVariableForCustomSettingLocation, EnvironmentVariableTarget.User) ?? FallBackCustomTestSettingsFileLocation;

            var codeBaseUrl = new Uri(Assembly.GetExecutingAssembly().Location);
            var codeBasePath = Uri.UnescapeDataString(codeBaseUrl.AbsolutePath);
            var dirPath = Path.GetDirectoryName(codeBasePath);
            var customFileLoc = Path.Combine(dirPath, customTestSettingsFileLocation);

            var finalSettings = JObject.FromObject(defaultSettings);
            if (File.Exists(customFileLoc))
            {
                //TODO: Print that parsing custom values...
                var mergeSettings = new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Union };
                var customSettingsJson = JObject.Parse(File.ReadAllText(customFileLoc));
                customSettingsJson.Merge(finalSettings, mergeSettings);
                finalSettings = customSettingsJson;
            }

            return finalSettings.ToObject<TestEnvironmentSettings>();
        }

        /// <summary>
        /// Checks if a given storage is reachable.
        /// </summary>
        /// <param name="connection">The connection to check.</param>
        /// <returns></returns>
        private static async Task<bool> CanConnectToStorage(StorageConnection connection)
        {
            //How detect if a database can be connected is surprisingly tricky. Some information at
            //http://stackoverflow.com/questions/3668506/efficient-sql-test-query-or-validation-query-that-will-work-across-all-or-most.
            var storage = RelationalStorage.CreateInstance(connection.StorageInvariant, connection.ConnectionString);
            var query = connection.ConnectionString != AdoNetInvariants.InvariantNameOracleDatabase ? "SELECT 1;" : "SELECT 1 FROM DUAL;";
            try
            {
                await storage.ExecuteAsync(query);
            }
            catch
            {
                return false;
            }

            return true;
        }
    }
}
