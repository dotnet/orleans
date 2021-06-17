using OneBoxDeployment.Utilities;
using OneBoxDeployment.Api;
using OneBoxDeployment.IntegrationTests.HttpClients;
using OneBoxDeployment.OrleansUtilities;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Polly;
using Polly.Extensions.Http;
using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Program = OneBoxDeployment.Api.Program;
using System.Net.Mime;
using System.Text.Json;

namespace OneBoxDeployment.IntegrationTests
{
    /// <summary>
    /// Running silo and its configuration information.
    /// </summary>
    public sealed class RunningSilo
    {
        /// <summary>
        /// The running silo.
        /// </summary>
        public ISiloHost Silo { get; }

        /// <summary>
        /// The configuration used to start the silo.
        /// </summary>
        public ClusterConfig ClusterConfig { get; }


        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="silo">The running silo.</param>
        /// <param name="clusterConfig">The cluster configuration used to start the silo.</param>
        public RunningSilo(ISiloHost silo, ClusterConfig clusterConfig)
        {
            Silo = silo ?? throw new ArgumentNullException(nameof(silo));
            ClusterConfig = clusterConfig ?? throw new ArgumentNullException(nameof(clusterConfig));
        }
    }

    /// <summary>
    /// A fixture to create <see cref="IWebHost"/> from <see cref="Startup"/> for testing purposes.
    /// </summary>
    public class IntegrationTestFixture: IAsyncLifetime
    {
        /// <summary>
        /// The OneBoxDeployment Web API server.
        /// </summary>
        private IWebHost ApiHost { get; set; }

        /// <summary>
        /// The OneBoxDeployment identity server.
        /// </summary>
        private IWebHost IdentityHost { get; set; }

        /// <summary>
        /// The currently running Orleans siloes.
        /// </summary>
        private static List<RunningSilo> Siloes { get; } = new List<RunningSilo>();

        /// <summary>
        /// The database name.
        /// </summary>
        private static string DatabaseName { get; } = "OneBoxDeployment.Database";

        /// <summary>
        /// The database connection string.
        /// </summary>
        private static string DatabaseConnectionString { get; set; }

        /// <summary>
        /// The database snapshot name.
        /// </summary>
        private static string DatabaseSnapshotName { get; set; }

        private static IManagementQueries DatabaseManagementQueries { get; } = new SqlServerManagementQueries();

        /// <summary>
        /// All siloes and clients in the cluster find each other using this shared value
        /// in the membership table.
        /// </summary>
        /// <remarks>The database snapshotting makes sure the cluster identifiers never overlap
        /// since originally they are not there and snapshot restore further removes them.</remarks>
        public string ClusterId { get; } = "TestCluster-" + DateTime.UtcNow.ToString("O");

        /// <summary>
        /// This should remain stable between deployments. Reminders and storage use this as the key
        /// so they can find the data even across cluster deployments.
        /// </summary>
        /// <remarks>The database snapshotting makes sure the service identifiers never overlap
        /// since originally they are not there and snapshot restore further removes them.</remarks>
        public string ServiceId { get; } = "TestService-" + DateTime.UtcNow.ToString("O");

        /// <summary>
        /// A service collection for various constructed IoC services.
        /// </summary>
        public IServiceProvider ServicesProvider { get; }

        /// <summary>
        /// The default Data API root URL, including the port.
        /// </summary>
        public string DefaultApiRootUrl { get; set; }

        /// <summary>
        /// The default Identity API root URL, including the port.
        /// </summary>
        public string DefaultIdentityRootUrl { get; set; }

        /// <summary>
        /// This route can be used to test pipeline exception handling during tests.
        /// </summary>
        public const string FaultyRouteUrlFragment = "/internalservererror";

        /// <summary>
        /// These are testing only default extra parameters that should not be present in a running application.
        /// </summary>
        /// <remarks>These settings are used, amongst others, to control fault injection in tests.</remarks>
        public Dictionary<string, string> DefaultExtraParameters { get; } = new Dictionary<string, string>
        {
            //This setting creates a route that will always throw an exception. It's injected with a
            //a setting to not to create the route in production. This is built like this also so that
            //there would not be a path to accidentally inject faulty code to production.
            { ConfigurationKeys.AlwaysFaultyRoute, FaultyRouteUrlFragment }
        };

        /// <summary>
        /// The test message sink.
        /// </summary>
        public IMessageSink MessageSink { get; }

        /// <summary>
        /// The in-memory storage of logs in the whole system.
        /// </summary>
        public InMemoryLoggerProvider InMemoryLoggerProvider { get; } = new InMemoryLoggerProvider();


        /// <summary>
        /// A default constructor.
        /// </summary>
        /// <param name="messageSink">The test message sink.</param>
        public IntegrationTestFixture(IMessageSink messageSink)
        {
            MessageSink = messageSink;

            //Sets this to testing environment if not in "Production".
            const string AspNetCoreEnvironment = "ASPNETCORE_ENVIRONMENT";
            if(Environment.GetEnvironmentVariable(AspNetCoreEnvironment) == null)
            {
                Environment.SetEnvironmentVariable(AspNetCoreEnvironment, "Development");
            }

            var environmentName = Environment.GetEnvironmentVariable(AspNetCoreEnvironment);
            var configuration = new ConfigurationBuilder()
               .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
               .AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true)
               .AddEnvironmentVariables()
               .Build();

            //Collect the database names.
            DatabaseConnectionString = configuration.GetValue<string>($"ConnectionStrings:{DatabaseName}");
            DatabaseSnapshotName = $"{DatabaseName}_Snapshot_{DateTime.UtcNow.ToString("O")}";

            //The API test server is started in a random free port in order to avoid collisions with the already running APIs,
            //identity or other services. Also connects to the identity server in the right ports.
            int freeEphemeralApiPort = PlatformUtilities.GetFreePortFromEphemeralRange();
            DefaultApiRootUrl = $"http://localhost:{freeEphemeralApiPort}/";
            int freeEphemeralIdentityPort = PlatformUtilities.GetFreePortFromEphemeralRange();
            DefaultIdentityRootUrl = $"http://localhost:{freeEphemeralIdentityPort}/";

            void onBreak(DelegateResult<HttpResponseMessage> handledFault, TimeSpan duration) =>  Console.WriteLine("temp"); /* Logger.LogError($"Circuit breaking for {duration} due to {handledFault.Exception?.Message ?? handledFault.Result.StatusCode.ToString()}"); */
            void onReset() { /* etc */ }
            var circuitBreaker = HttpPolicyExtensions
                .HandleTransientHttpError()
                .AdvancedCircuitBreakerAsync(
                    failureThreshold: 0.5,
                    samplingDuration: TimeSpan.FromSeconds(10),
                    minimumThroughput: 2,
                    durationOfBreak: TimeSpan.FromMilliseconds(300),
                    onBreak: onBreak,
                    onReset: onReset);

            var services = new ServiceCollection();
            services.AddTypedHttpClient<FaultyRouteClient>(httpClient =>
            {
                httpClient.BaseAddress = new Uri(new Uri(DefaultApiRootUrl), FaultyRouteUrlFragment);
                httpClient.DefaultRequestHeaders.Add("Accept", MediaTypeNames.Application.Json);
                httpClient.DefaultRequestHeaders.Add("x-correlation-id", "test123");
            })
            .AddHttpMessageHandler(_ => new SecurityHeaderTestMessageHandler());

            services.AddTypedHttpClient<CspClient>(httpClient =>
            {
                httpClient.BaseAddress = new Uri(DefaultApiRootUrl);
                httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
                httpClient.DefaultRequestHeaders.Add("x-correlation-id", "test123");
            })
            .AddHttpMessageHandler(_ => new SecurityHeaderTestMessageHandler());

            services.AddTypedHttpClient<TestStateClient>(httpClient =>
            {
                httpClient.BaseAddress = new Uri(DefaultApiRootUrl);
                httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
                httpClient.DefaultRequestHeaders.Add("x-correlation-id", "test123");
            })
            .AddHttpMessageHandler(_ => new SecurityHeaderTestMessageHandler());

            DefaultExtraParameters["AuthorityUrl"] = DefaultIdentityRootUrl;

            ServicesProvider = services.BuildServiceProvider(validateScopes: true);
        }


        /// <summary>
        /// <see cref="IAsyncLifetime.InitializeAsync"/>
        /// </summary>
        public async Task InitializeAsync()
        {
            //Preconditions checks without which the tests cannot be started.
            if(!await ExistsDatabase(DatabaseName).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Database \"{DatabaseName}\" does not exist.");
            }

            //First a database snapshot that can be restored after the tests. This carries
            //also the developer made changes since it may the purpose of the tests to test them.
            //The database can be reset before running tests.
            await CreateDatabaseSnapshot(DatabaseName, DatabaseSnapshotName).ConfigureAwait(false);

            //By default two local siloes are started. One configuration object is saved to be given
            //to the client. The siloes are started first so that the cluster clients won't hang
            //while waiting for the clusters to start (they're in the same process). See
            //ClusterClientStartupTask for improvements on asynchronous tasks in ASP.NET Core.
            var siloConfig = CreateClusterConfig(ClusterId, ServiceId);
            var silo1 = BuildSilo(siloConfig);
            var silo2 = BuildSilo(CreateClusterConfig(ClusterId, ServiceId));
            await Task.WhenAll(silo1.StartAsync(), silo2.StartAsync()).ConfigureAwait(false);

            var serializeOptions = new JsonSerializerOptions();
            serializeOptions.Converters.Add(new IPAddressConverter());
            DefaultExtraParameters.Add(nameof(ClusterConfig), JsonSerializer.Serialize(siloConfig, serializeOptions));
            ApiHost = Program.InternalBuildWebHost(new[] { "--server.urls", DefaultApiRootUrl }, DefaultExtraParameters, new[] { InMemoryLoggerProvider });
            var apiServerTask = ApiHost.StartAsync();

            //This also adds well known user names to the the system. The password is "Foobar".
            //var wellKnownTestUsers = JsonConvert.SerializeObject(GetWellKnownTestUsers(), Formatting.Indented);
            //IdentityHost = Identity.Program.InternalBuildWebHost(new[] { "--server.urls", DefaultIdentityRootUrl }, new Dictionary<string, string> { { "WellKnownTestUsers", wellKnownTestUsers }, { "integrationtest", "yes" } });
            //var identityServerTask = IdentityHost.StartAsync();

            await Task.WhenAll(apiServerTask/*, identityServerTask*/).ConfigureAwait(false);
        }


        /// <summary>
        /// Creates a Orleans cluster configuration.
        /// </summary>
        /// <param name="clusterId">The cluster identifier.</param>
        /// <param name="serviceId">The service identifier.</param>
        /// <returns>A new cluster configuration.</returns>
        public static ClusterConfig CreateClusterConfig(string clusterId, string serviceId)
        {
            const string AdoNetInvariant = "Microsoft.Data.SqlClient";
            int GatewayPort = PlatformUtilities.GetFreePortFromEphemeralRange();
            int SiloPort = PlatformUtilities.GetFreePortFromEphemeralRange();
            return new ClusterConfig
            {
                ClusterOptions = new ClusterOptions
                {
                    ClusterId = clusterId,
                    ServiceId = serviceId,
                },
                EndPointOptions = new EndpointOptions
                {
                    AdvertisedIPAddress = IPAddress.Loopback,
                    GatewayPort = GatewayPort,
                    SiloPort = SiloPort
                },
                ConnectionConfig = new ConnectionConfig
                {
                    Name = "TestCluster",
                    AdoNetConstant = AdoNetInvariant,
                    ConnectionString = DatabaseConnectionString
                },
                StorageConfigs = new List<ConnectionConfig>(new[]
                {
                    new ConnectionConfig
                    {
                        Name = "TestStorage",
                        AdoNetConstant = AdoNetInvariant,
                        ConnectionString = DatabaseConnectionString
                    }
                }),
                ReminderConfigs = new List<ConnectionConfig>(new[]
                {
                    new ConnectionConfig
                    {
                        Name = "TestReminders",
                        AdoNetConstant = AdoNetInvariant,
                        ConnectionString = DatabaseConnectionString
                    }
                })
            };
        }


        /// <summary>
        /// Builds a new Orleans silo.
        /// </summary>
        /// <returns></returns>
        public static ISiloHost BuildSilo(ClusterConfig clusterConfig)
        {
            var silo = OneBoxDeployment.OrleansHost.Program.BuildOrleansHost(null, clusterConfig);
            Siloes.Add(new RunningSilo(silo, clusterConfig));

            return silo;
        }


        /// <summary>
        /// <see cref="IAsyncLifetime.DisposeAsync"/>
        /// </summary>
        public async Task DisposeAsync()
        {
            try
            {
                await Task.WhenAll(
                    Task.Run(() => Siloes.ForEach(rs => rs.Silo.Dispose())),
                    Task.Run(() => ApiHost?.Dispose()),
                    Task.Run(() => IdentityHost?.Dispose())).ConfigureAwait(false);
            }
            finally
            {
                await RestoreAndDropDatabaseSnapshot(DatabaseName, DatabaseSnapshotName).ConfigureAwait(false);
            }
        }


        /// <summary>
        /// Checks existence of <paramref name="databaseName"/>.
        /// </summary>
        /// <param name="databaseName">The name of the database.</param>
        private static async Task<bool> ExistsDatabase(string databaseName)
        {
            using(var connection = new SqlConnection(DatabaseConnectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);

                var existsDatabaseQuery = DatabaseManagementQueries.ExistsDatabase(databaseName);
                return (await connection.QueryAsync<bool>(existsDatabaseQuery).ConfigureAwait(false)).Single();
            }
        }


        /// <summary>
        /// Creates a database snapshot.
        /// </summary>
        /// <param name="databaseName">The name of the database the snapshot is taken from.</param>
        /// <param name="databaseSnapshotName">The name of the snapshot database.</param>
        private async static Task CreateDatabaseSnapshot(string databaseName, string databaseSnapshotName)
        {
            using(var connection = new SqlConnection(DatabaseConnectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);

                var createDatabaseSnapshotQuery = DatabaseManagementQueries.CreateDatabaseSnapshot(databaseName, databaseSnapshotName);
                await connection.QueryAsync<int>(createDatabaseSnapshotQuery).ConfigureAwait(false);
            }
        }


        /// <summary>
        /// Restores database from a snapshot.
        /// </summary>
        /// <param name="databaseName">The name of the database which to restore.</param>
        /// <param name="databaseSnapshotName">The name of the snapshot database from which to restore.</param>
        private async static Task RestoreAndDropDatabaseSnapshot(string databaseName, string databaseSnapshotName)
        {
            using(var connection = new SqlConnection(DatabaseConnectionString))
            {
                await connection.OpenAsync().ConfigureAwait(false);

                var restoreDatabaseSnapshotQuery = DatabaseManagementQueries.RestoreDatabaseFromSnapshot(databaseName, databaseSnapshotName);
                var dropDatabaseSnapshotQuery = DatabaseManagementQueries.DropDatabaseSnapshot(databaseSnapshotName);
                await connection.QueryAsync<int>(restoreDatabaseSnapshotQuery).ConfigureAwait(false);
                await connection.QueryAsync<int>(dropDatabaseSnapshotQuery).ConfigureAwait(false);
            }
        }
    }
}
