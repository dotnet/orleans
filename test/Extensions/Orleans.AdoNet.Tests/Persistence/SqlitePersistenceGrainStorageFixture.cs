using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Storage;
using Orleans.Tests.SqlUtils;
using TestExtensions;

namespace Tester.AdoNet.Persistence
{
    public sealed class SqlitePersistenceGrainStorageFixture : TestEnvironmentFixture
    {
        public const string AdoInvariant = AdoNetInvariants.InvariantNameSqlLite;

        public SqlitePersistenceGrainStorageFixture()
        {
            this.DatabaseFilePath = Path.Combine(Path.GetTempPath(), $"orleans-sqlite-persistence-{Guid.NewGuid():N}.db");
            this.ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = this.DatabaseFilePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            this.DatabaseStorage = RelationalStorage.CreateInstance(AdoInvariant, this.ConnectionString);
            this.InitializeSchemaAsync().GetAwaiter().GetResult();
            this.Storage = this.CreateGrainStorageAsync().GetAwaiter().GetResult();
        }

        public string DatabaseFilePath { get; }

        public string ConnectionString { get; }

        public IRelationalStorage DatabaseStorage { get; }

        public AdoNetGrainStorage Storage { get; }

        public async Task InitializeSchemaAsync()
        {
            await this.DatabaseStorage.ExecuteAsync(await LoadScriptAsync("Sqlite-Main.sql"), command => { }).ConfigureAwait(false);
            await this.DatabaseStorage.ExecuteAsync(await LoadScriptAsync("Sqlite-Persistence.sql"), command => { }).ConfigureAwait(false);
        }

        public async Task<AdoNetGrainStorage> CreateGrainStorageAsync(string storageName = "SqliteGrainStorageForTest")
        {
            var providerRuntime = new ClientProviderRuntime(
                this.InternalGrainFactory,
                this.Services,
                this.Services.GetRequiredService<ClientGrainContext>());

            var options = new AdoNetGrainStorageOptions
            {
                ConnectionString = this.ConnectionString,
                Invariant = AdoInvariant,
                GrainStorageSerializer = new JsonGrainStorageSerializer(providerRuntime.ServiceProvider.GetService<OrleansJsonSerializer>())
            };

            var storageProvider = new AdoNetGrainStorage(
                providerRuntime.ServiceProvider.GetRequiredService<IActivatorProvider>(),
                providerRuntime.ServiceProvider.GetRequiredService<ILogger<AdoNetGrainStorage>>(),
                Options.Create(options),
                Options.Create(new ClusterOptions { ServiceId = Guid.NewGuid().ToString() }),
                storageName);

            ISiloLifecycleSubject siloLifeCycle = new SiloLifecycleSubject(NullLoggerFactory.Instance.CreateLogger<SiloLifecycleSubject>());
            storageProvider.Participate(siloLifeCycle);
            await siloLifeCycle.OnStart(CancellationToken.None).ConfigureAwait(false);
            return storageProvider;
        }

        private static async Task<string> LoadScriptAsync(string fileName)
        {
            var scriptPath = Path.Combine(AppContext.BaseDirectory, fileName);
            if (!File.Exists(scriptPath))
            {
                scriptPath = Path.Combine(Environment.CurrentDirectory, fileName);
            }

            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"Unable to locate SQL script '{fileName}'.", fileName);
            }

            return await File.ReadAllTextAsync(scriptPath).ConfigureAwait(false);
        }
    }
}
