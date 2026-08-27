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
using Xunit;

namespace Tester.AdoNet.Persistence
{
    public sealed class SqlitePersistenceGrainStorageFixture : TestEnvironmentFixture, IAsyncLifetime
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
        }

        public string DatabaseFilePath { get; }

        public string ConnectionString { get; }

        public IRelationalStorage DatabaseStorage { get; }

        public AdoNetGrainStorage Storage { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await this.InitializeSchemaAsync(cancellationToken);
            this.Storage = await this.CreateGrainStorageAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task InitializeSchemaAsync(CancellationToken cancellationToken)
        {
            await this.DatabaseStorage.ExecuteAsync(
                await LoadScriptAsync("Sqlite-Main.sql", cancellationToken),
                command => { },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await this.DatabaseStorage.ExecuteAsync(
                await LoadScriptAsync("Sqlite-Persistence.sql", cancellationToken),
                command => { },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public async Task<AdoNetGrainStorage> CreateGrainStorageAsync(
            CancellationToken cancellationToken,
            string storageName = "SqliteGrainStorageForTest")
        {
            var providerRuntime = new ClientProviderRuntime(
                this.InternalGrainFactory,
                this.Services,
                this.Services.GetRequiredService<ClientGrainContext>());

            var options = new AdoNetGrainStorageOptions
            {
                ConnectionString = this.ConnectionString,
                Invariant = AdoInvariant,
                GrainStorageSerializer = new JsonGrainStorageSerializer(providerRuntime.ServiceProvider.GetService<OrleansJsonSerializer>()!)
            };

            var storageProvider = new AdoNetGrainStorage(
                providerRuntime.ServiceProvider.GetRequiredService<IActivatorProvider>(),
                providerRuntime.ServiceProvider.GetRequiredService<ILogger<AdoNetGrainStorage>>(),
                Options.Create(options),
                Options.Create(new ClusterOptions { ServiceId = Guid.NewGuid().ToString() }),
                storageName);

            ISiloLifecycleSubject siloLifeCycle = new SiloLifecycleSubject(NullLoggerFactory.Instance.CreateLogger<SiloLifecycleSubject>());
            storageProvider.Participate(siloLifeCycle);
            await siloLifeCycle.OnStart(cancellationToken).ConfigureAwait(false);
            return storageProvider;
        }

        private static async Task<string> LoadScriptAsync(string fileName, CancellationToken cancellationToken)
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

            return await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
        }

        public async Task<AdoNetGrainStorage> CreateGrainStorageAsync(
            System.Data.Common.DbDataSource dataSource,
            CancellationToken cancellationToken,
            string storageName = "SqliteDataSourceGrainStorageForTest")
        {
            var providerRuntime = new ClientProviderRuntime(
                this.InternalGrainFactory,
                this.Services,
                this.Services.GetRequiredService<ClientGrainContext>());

            var options = new AdoNetGrainStorageOptions
            {
                DataSource = dataSource,
                Invariant = AdoInvariant,
                GrainStorageSerializer = new JsonGrainStorageSerializer(providerRuntime.ServiceProvider.GetService<OrleansJsonSerializer>()!)
            };

            var storageProvider = new AdoNetGrainStorage(
                providerRuntime.ServiceProvider.GetRequiredService<IActivatorProvider>(),
                providerRuntime.ServiceProvider.GetRequiredService<ILogger<AdoNetGrainStorage>>(),
                Options.Create(options),
                Options.Create(new ClusterOptions { ServiceId = Guid.NewGuid().ToString() }),
                storageName);

            ISiloLifecycleSubject siloLifeCycle = new SiloLifecycleSubject(NullLoggerFactory.Instance.CreateLogger<SiloLifecycleSubject>());
            storageProvider.Participate(siloLifeCycle);
            await siloLifeCycle.OnStart(cancellationToken).ConfigureAwait(false);
            return storageProvider;
        }
    }
}
