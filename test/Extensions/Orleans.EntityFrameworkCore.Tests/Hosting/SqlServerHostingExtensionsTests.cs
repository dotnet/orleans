using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Clustering.EntityFrameworkCore;
using Orleans.Clustering.EntityFrameworkCore.SqlServer;
using Orleans.Clustering.EntityFrameworkCore.SqlServer.Data;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.EntityFrameworkCore;
using Orleans.GrainDirectory.EntityFrameworkCore.SqlServer.Data;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Persistence;
using Orleans.Persistence.EntityFrameworkCore;
using Orleans.Persistence.EntityFrameworkCore.SqlServer.Data;
using Orleans.Reminders;
using Orleans.Reminders.EntityFrameworkCore;
using Orleans.Reminders.EntityFrameworkCore.SqlServer.Data;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Hosting;

[TestArea("EFCore")]
[TestProvider("None")]
[TestSuite("BVT")]
public sealed class SqlServerHostingExtensionsTests
{
    private const string ConnectionString =
        "Server=localhost;Database=hosting;User ID=test;Password=pass;TrustServerCertificate=True";

    [Fact]
    public void ClusteringSilo_ConfiguredOverload_RegistersFactoryConverterAndMembership()
    {
        using var host = new HostBuilder()
            .UseOrleans(builder =>
            {
                var result = Orleans.Clustering.SqlServerHostingExtensions
                    .UseEntityFrameworkCoreSqlServerClustering(builder, ConfigureDatabase());

                Assert.Same(builder, result);
            })
            .Build();

        Assert.NotNull(host.Services.GetRequiredService<IDbContextFactory<SqlServerClusterDbContext>>());
        Assert.IsType<EFMembershipTable<SqlServerClusterDbContext, byte[]>>(
            host.Services.GetRequiredService<IMembershipTable>());
        Assert.IsType<SqlServerClusterETagConverter>(
            host.Services.GetRequiredService<IEFClusterETagConverter<byte[]>>());
    }

    [Fact]
    public void ClusteringClient_PreRegisteredFactoryOverload_RegistersConverterAndGateway()
    {
        var services = new ServiceCollection();
        var builder = new ClientBuilder(services, new ConfigurationBuilder().Build());
        services.AddPooledDbContextFactory<SqlServerClusterDbContext>(ConfigureDatabase());

        var result = Orleans.Clustering.SqlServerHostingExtensions
            .UseEntityFrameworkCoreSqlServerClustering(builder);

        Assert.Same(builder, result);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IGatewayListProvider)
                && descriptor.ImplementationType == typeof(EFGatewayListProvider<SqlServerClusterDbContext, byte[]>));
        using var serviceProvider = services.BuildServiceProvider();
        Assert.IsType<SqlServerClusterETagConverter>(
            serviceProvider.GetRequiredService<IEFClusterETagConverter<byte[]>>());
        Assert.NotNull(serviceProvider.GetRequiredService<IDbContextFactory<SqlServerClusterDbContext>>());
    }

    [Fact]
    public void GrainDirectory_ConfiguredAndPreRegisteredOverloads_PreserveNamedRegistrations()
    {
        const string configuredName = "configured-directory";
        const string preRegisteredName = "pre-registered-directory";
        using var host = new HostBuilder()
            .UseOrleans(builder =>
            {
                builder.Services.AddPooledDbContextFactory<SqlServerGrainDirectoryDbContext>(ConfigureDatabase());

                var configuredResult = Orleans.GrainDirectory.SqlServerHostingExtensions
                    .AddEntityFrameworkCoreSqlServerGrainDirectory(builder, configuredName, ConfigureDatabase());
                var preRegisteredResult = Orleans.GrainDirectory.SqlServerHostingExtensions
                    .AddEntityFrameworkCoreSqlServerGrainDirectory(builder, preRegisteredName);

                Assert.Same(builder, configuredResult);
                Assert.Same(builder, preRegisteredResult);
            })
            .Build();

        var configured = host.Services.GetRequiredKeyedService<IGrainDirectory>(configuredName);
        var preRegistered = host.Services.GetRequiredKeyedService<IGrainDirectory>(preRegisteredName);
        Assert.NotSame(configured, preRegistered);
        Assert.IsType<EFCoreGrainDirectory<SqlServerGrainDirectoryDbContext, byte[]>>(configured);
        Assert.IsType<EFCoreGrainDirectory<SqlServerGrainDirectoryDbContext, byte[]>>(preRegistered);
        Assert.IsType<SqlServerGrainDirectoryETagConverter>(
            host.Services.GetRequiredService<IEFGrainDirectoryETagConverter<byte[]>>());
        Assert.NotNull(host.Services.GetRequiredService<IDbContextFactory<SqlServerGrainDirectoryDbContext>>());
    }

    [Fact]
    public void GrainStorage_ServiceAndBuilderOverloads_RegisterDistinctNamedProviders()
    {
        const string serviceName = "service-storage";
        const string builderName = "builder-storage";
        using var host = new HostBuilder()
            .UseOrleans(builder =>
            {
                builder.Services.AddPooledDbContextFactory<SqlServerGrainStateDbContext>(ConfigureDatabase());
                RegisterStorageSerializer(builder.Services);

                var servicesResult = Orleans.Persistence.SqlHostingExtensions
                    .AddEntityFrameworkCoreSqlServerGrainStorage(builder.Services, serviceName);
                var builderResult = Orleans.Persistence.SqlHostingExtensions
                    .AddEntityFrameworkCoreSqlServerGrainStorage(builder, builderName, ConfigureDatabase());

                Assert.Same(builder.Services, servicesResult);
                Assert.Same(builder, builderResult);
            })
            .Build();

        var fromServices = host.Services.GetRequiredKeyedService<IGrainStorage>(serviceName);
        var fromBuilder = host.Services.GetRequiredKeyedService<IGrainStorage>(builderName);
        Assert.NotSame(fromServices, fromBuilder);
        Assert.IsType<EFGrainStorage<SqlServerGrainStateDbContext, byte[]>>(fromServices);
        Assert.IsType<EFGrainStorage<SqlServerGrainStateDbContext, byte[]>>(fromBuilder);
        Assert.IsType<SqlServerGrainStateETagConverter>(
            host.Services.GetRequiredService<IEFGrainStorageETagConverter<byte[]>>());
        Assert.NotNull(host.Services.GetRequiredService<IDbContextFactory<SqlServerGrainStateDbContext>>());
    }

    [Fact]
    public async Task ConfiguredNamedProviders_UseIsolatedDbContextFactories()
    {
        const string firstStorage = "first-storage";
        const string secondStorage = "second-storage";
        const string firstDirectory = "first-directory";
        const string secondDirectory = "second-directory";
        using var host = new HostBuilder()
            .UseOrleans(builder =>
            {
                RegisterStorageSerializer(builder.Services);
                Orleans.Persistence.SqlHostingExtensions.AddEntityFrameworkCoreSqlServerGrainStorage(
                    builder,
                    firstStorage,
                    ConfigureDatabase("storage_one"));
                Orleans.Persistence.SqlHostingExtensions.AddEntityFrameworkCoreSqlServerGrainStorage(
                    builder,
                    secondStorage,
                    ConfigureDatabase("storage_two"));
                Orleans.GrainDirectory.SqlServerHostingExtensions.AddEntityFrameworkCoreSqlServerGrainDirectory(
                    builder,
                    firstDirectory,
                    ConfigureDatabase("directory_one"));
                Orleans.GrainDirectory.SqlServerHostingExtensions.AddEntityFrameworkCoreSqlServerGrainDirectory(
                    builder,
                    secondDirectory,
                    ConfigureDatabase("directory_two"));
            })
            .Build();

        Assert.Equal(
            ["storage_one", "storage_two"],
            await GetDatabaseNames<SqlServerGrainStateDbContext>(host.Services, firstStorage, secondStorage));
        Assert.Equal(
            ["directory_one", "directory_two"],
            await GetDatabaseNames<SqlServerGrainDirectoryDbContext>(host.Services, firstDirectory, secondDirectory));
    }

    [Fact]
    public void Reminder_ServiceAndBuilderOverloads_RegisterFactoryConverterAndReminderTable()
    {
        using var host = new HostBuilder()
            .UseOrleans(builder =>
            {
                builder.Services.AddPooledDbContextFactory<SqlServerReminderDbContext>(ConfigureDatabase());

                var servicesResult = Orleans.Reminders.SqlHostingExtensions
                    .UseEntityFrameworkCoreSqlServerReminderService(builder.Services);
                var builderResult = Orleans.Reminders.SqlHostingExtensions
                    .UseEntityFrameworkCoreSqlServerReminderService(builder, ConfigureDatabase());

                Assert.Same(builder.Services, servicesResult);
                Assert.Same(builder, builderResult);
            })
            .Build();

        Assert.IsType<SqlServerReminderETagConverter>(
            host.Services.GetRequiredService<IEFReminderETagConverter<byte[]>>());
        Assert.IsType<EFReminderTable<SqlServerReminderDbContext, byte[]>>(
            host.Services.GetRequiredService<IReminderTable>());
        Assert.NotNull(host.Services.GetRequiredService<IDbContextFactory<SqlServerReminderDbContext>>());
    }

    private static Action<DbContextOptionsBuilder> ConfigureDatabase(string databaseName = "hosting") =>
        options => EFCoreTestDatabase.SqlServer.ConfigureOptions(
            options,
            EFCoreTestDatabase.SqlServer.WithDatabase(ConnectionString, databaseName),
            typeof(SqlServerHostingExtensionsTests).Assembly.GetName().Name!);

    private static async Task<string[]> GetDatabaseNames<TDbContext>(
        IServiceProvider services,
        params string[] names)
        where TDbContext : DbContext
    {
        var result = new string[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            await using var context = await services
                .GetRequiredKeyedService<IDbContextFactory<TDbContext>>(names[i])
                .CreateDbContextAsync();
            result[i] = context.Database.GetDbConnection().Database;
        }

        return result;
    }

    private static void RegisterStorageSerializer(IServiceCollection services) =>
        services.AddSingleton<IGrainStorageSerializer>(
            new SystemTextJsonGrainStorageSerializer(
                Microsoft.Extensions.Options.Options.Create(
                    new SystemTextJsonGrainStorageSerializerOptions())));
}
