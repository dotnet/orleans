using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Clustering.EntityFrameworkCore;
using Orleans.Clustering.EntityFrameworkCore.MySql.Data;
using Orleans.Clustering.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.EntityFrameworkCore.Tests.Infrastructure;
using Orleans.GrainDirectory;
using Orleans.GrainDirectory.EntityFrameworkCore;
using Orleans.GrainDirectory.EntityFrameworkCore.MySql.Data;
using Orleans.GrainDirectory.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Persistence.EntityFrameworkCore;
using Orleans.Persistence.EntityFrameworkCore.MySql.Data;
using Orleans.Persistence.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.Reminders.EntityFrameworkCore;
using Orleans.Reminders.EntityFrameworkCore.MySql.Data;
using Orleans.Reminders.EntityFrameworkCore.PostgreSQL.Data;
using Orleans.Runtime;
using Orleans.Storage;
using TestExtensions;

namespace Orleans.EntityFrameworkCore.Tests.Hosting;

[TestCategory(EFCoreTestCategories.Unit)]
public sealed class EFCoreHostingExtensionsTests
{
    [Fact]
    public void GrainStorageAsDefaultOverloadsDoNotAcceptProviderNames()
    {
        var overloads = typeof(Orleans.Persistence.EFGrainStorageHostingExtensions)
            .GetMethods()
            .Where(method => method.Name == "AddEntityFrameworkCoreGrainStorageAsDefault")
            .ToArray();

        Assert.Equal(4, overloads.Length);
        Assert.All(
            overloads,
            method => Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(string)));
    }

    [Theory]
    [InlineData(GuidProvider.MySql)]
    [InlineData(GuidProvider.PostgreSql)]
    public void ClusteringSilo_ConfiguredOverload_RegistersFactoryConverterAndMembership(GuidProvider provider)
    {
        using var host = new HostBuilder()
            .UseOrleans(builder =>
            {
                var result = UseClustering(builder, provider, GetDatabaseConfiguration(provider));

                Assert.Same(builder, result);
            })
            .Build();

        if (provider is GuidProvider.MySql)
        {
            Assert.NotNull(host.Services.GetRequiredService<IDbContextFactory<MySqlClusterDbContext>>());
            Assert.IsType<EFMembershipTable<MySqlClusterDbContext, Guid>>(
                host.Services.GetRequiredService<IMembershipTable>());
        }
        else
        {
            Assert.NotNull(host.Services.GetRequiredService<IDbContextFactory<PostgreSqlClusterDbContext>>());
            Assert.IsType<EFMembershipTable<PostgreSqlClusterDbContext, Guid>>(
                host.Services.GetRequiredService<IMembershipTable>());
        }

        Assert.IsType<GuidClusterETagConverter>(
            host.Services.GetRequiredService<IEFClusterETagConverter<Guid>>());
    }

    [Theory]
    [InlineData(GuidProvider.MySql)]
    [InlineData(GuidProvider.PostgreSql)]
    public void ClusteringClient_PreRegisteredFactoryOverload_RegistersConverterAndGateway(GuidProvider provider)
    {
        var services = new ServiceCollection();
        var builder = new ClientBuilder(services, new ConfigurationBuilder().Build());
        RegisterClusteringFactory(services, provider, GetDatabaseConfiguration(provider));

        var result = UseClustering(builder, provider);

        Assert.Same(builder, result);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IGatewayListProvider)
                && descriptor.ImplementationType == GetGatewayImplementationType(provider));
        using var serviceProvider = services.BuildServiceProvider();
        Assert.IsType<GuidClusterETagConverter>(
            serviceProvider.GetRequiredService<IEFClusterETagConverter<Guid>>());
        AssertClusteringFactoryRegistered(serviceProvider, provider);
    }

    [Theory]
    [InlineData(GuidProvider.MySql)]
    [InlineData(GuidProvider.PostgreSql)]
    public void GrainDirectory_ConfiguredAndPreRegisteredOverloads_PreserveNamedRegistrations(GuidProvider provider)
    {
        const string configuredName = "configured-directory";
        const string preRegisteredName = "pre-registered-directory";
        using var host = new HostBuilder()
            .UseOrleans(builder =>
            {
                RegisterGrainDirectoryFactory(builder.Services, provider, GetDatabaseConfiguration(provider));

                var configuredResult = AddGrainDirectory(
                    builder,
                    provider,
                    configuredName,
                    GetDatabaseConfiguration(provider));
                var preRegisteredResult = AddGrainDirectory(builder, provider, preRegisteredName);

                Assert.Same(builder, configuredResult);
                Assert.Same(builder, preRegisteredResult);
            })
            .Build();

        var configured = host.Services.GetRequiredKeyedService<IGrainDirectory>(configuredName);
        var preRegistered = host.Services.GetRequiredKeyedService<IGrainDirectory>(preRegisteredName);
        Assert.NotSame(configured, preRegistered);
        Assert.Equal(GetGrainDirectoryImplementationType(provider), configured.GetType());
        Assert.Equal(GetGrainDirectoryImplementationType(provider), preRegistered.GetType());
        Assert.IsType<GuidGrainDirectoryETagConverter>(
            host.Services.GetRequiredService<IEFGrainDirectoryETagConverter<Guid>>());
        AssertGrainDirectoryFactoryRegistered(host.Services, provider);
    }

    [Theory]
    [InlineData(GuidProvider.MySql)]
    [InlineData(GuidProvider.PostgreSql)]
    public void GrainStorage_ServiceAndBuilderOverloads_RegisterDistinctNamedProviders(GuidProvider provider)
    {
        const string serviceName = "service-storage";
        const string builderName = "builder-storage";
        using var host = new HostBuilder()
            .UseOrleans(builder =>
            {
                RegisterGrainStateFactory(builder.Services, provider, GetDatabaseConfiguration(provider));

                var servicesResult = AddGrainStorage(builder.Services, provider, serviceName);
                var builderResult = AddGrainStorage(
                    builder,
                    provider,
                    builderName,
                    GetDatabaseConfiguration(provider));

                Assert.Same(builder.Services, servicesResult);
                Assert.Same(builder, builderResult);
            })
            .Build();

        var fromServices = host.Services.GetRequiredKeyedService<IGrainStorage>(serviceName);
        var fromBuilder = host.Services.GetRequiredKeyedService<IGrainStorage>(builderName);
        Assert.NotSame(fromServices, fromBuilder);
        Assert.Equal(GetGrainStorageImplementationType(provider), fromServices.GetType());
        Assert.Equal(GetGrainStorageImplementationType(provider), fromBuilder.GetType());
        Assert.IsType<GuidGrainStorageETagConverter>(
            host.Services.GetRequiredService<IEFGrainStorageETagConverter<Guid>>());
        AssertGrainStateFactoryRegistered(host.Services, provider);
    }

    [Theory]
    [InlineData(GuidProvider.MySql)]
    [InlineData(GuidProvider.PostgreSql)]
    public void Reminder_ServiceAndBuilderOverloads_RegisterFactoryConverterAndReminderTable(GuidProvider provider)
    {
        using var host = new HostBuilder()
            .UseOrleans(builder =>
            {
                RegisterReminderFactory(builder.Services, provider, GetDatabaseConfiguration(provider));

                var servicesResult = UseReminderService(builder.Services, provider);
                var builderResult = UseReminderService(builder, provider, GetDatabaseConfiguration(provider));

                Assert.Same(builder.Services, servicesResult);
                Assert.Same(builder, builderResult);
            })
            .Build();

        Assert.IsType<GuidReminderETagConverter>(
            host.Services.GetRequiredService<IEFReminderETagConverter<Guid>>());
        Assert.Equal(
            GetReminderTableImplementationType(provider),
            host.Services.GetRequiredService<IReminderTable>().GetType());
        AssertReminderFactoryRegistered(host.Services, provider);
    }

    private static Action<DbContextOptionsBuilder> GetDatabaseConfiguration(GuidProvider provider) =>
        options =>
        {
            var database = provider is GuidProvider.MySql
                ? EFCoreTestDatabase.MySql
                : EFCoreTestDatabase.PostgreSql;
            var connectionString = provider is GuidProvider.MySql
                ? "Server=localhost;Database=hosting;User ID=test;Password=test"
                : "Host=localhost;Database=hosting;Username=test;Password=test";
            database.ConfigureOptions(options, connectionString, typeof(EFCoreHostingExtensionsTests).Assembly.GetName().Name!);
        };

    private static ISiloBuilder UseClustering(
        ISiloBuilder builder,
        GuidProvider provider,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        provider is GuidProvider.MySql
            ? Orleans.Clustering.MySqlHostingExtensions.UseEntityFrameworkCoreMySqlClustering(builder, configureDatabase)
            : Orleans.Clustering.PostgreSqlHostingExtensions.UseEntityFrameworkCorePostgreSqlClustering(builder, configureDatabase);

    private static IClientBuilder UseClustering(IClientBuilder builder, GuidProvider provider) =>
        provider is GuidProvider.MySql
            ? Orleans.Clustering.MySqlHostingExtensions.UseEntityFrameworkCoreMySqlClustering(builder)
            : Orleans.Clustering.PostgreSqlHostingExtensions.UseEntityFrameworkCorePostgreSqlClustering(builder);

    private static void RegisterClusteringFactory(
        IServiceCollection services,
        GuidProvider provider,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        if (provider is GuidProvider.MySql)
        {
            services.AddPooledDbContextFactory<MySqlClusterDbContext>(configureDatabase);
        }
        else
        {
            services.AddPooledDbContextFactory<PostgreSqlClusterDbContext>(configureDatabase);
        }
    }

    private static Type GetGatewayImplementationType(GuidProvider provider) =>
        provider is GuidProvider.MySql
            ? typeof(EFGatewayListProvider<MySqlClusterDbContext, Guid>)
            : typeof(EFGatewayListProvider<PostgreSqlClusterDbContext, Guid>);

    private static void AssertClusteringFactoryRegistered(IServiceProvider services, GuidProvider provider)
    {
        if (provider is GuidProvider.MySql)
        {
            Assert.NotNull(services.GetRequiredService<IDbContextFactory<MySqlClusterDbContext>>());
        }
        else
        {
            Assert.NotNull(services.GetRequiredService<IDbContextFactory<PostgreSqlClusterDbContext>>());
        }
    }

    private static ISiloBuilder AddGrainDirectory(
        ISiloBuilder builder,
        GuidProvider provider,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        provider is GuidProvider.MySql
            ? Orleans.GrainDirectory.MySqlHostingExtensions.AddEntityFrameworkCoreMySqlGrainDirectory(
                builder,
                name,
                configureDatabase)
            : Orleans.GrainDirectory.PostgreSqlHostingExtensions.AddEntityFrameworkCorePostgreSqlGrainDirectory(
                builder,
                name,
                configureDatabase);

    private static ISiloBuilder AddGrainDirectory(ISiloBuilder builder, GuidProvider provider, string name) =>
        provider is GuidProvider.MySql
            ? Orleans.GrainDirectory.MySqlHostingExtensions.AddEntityFrameworkCoreMySqlGrainDirectory(builder, name)
            : Orleans.GrainDirectory.PostgreSqlHostingExtensions.AddEntityFrameworkCorePostgreSqlGrainDirectory(builder, name);

    private static void RegisterGrainDirectoryFactory(
        IServiceCollection services,
        GuidProvider provider,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        if (provider is GuidProvider.MySql)
        {
            services.AddPooledDbContextFactory<MySqlGrainDirectoryDbContext>(configureDatabase);
        }
        else
        {
            services.AddPooledDbContextFactory<PostgreSqlGrainDirectoryDbContext>(configureDatabase);
        }
    }

    private static Type GetGrainDirectoryImplementationType(GuidProvider provider) =>
        provider is GuidProvider.MySql
            ? typeof(EFCoreGrainDirectory<MySqlGrainDirectoryDbContext, Guid>)
            : typeof(EFCoreGrainDirectory<PostgreSqlGrainDirectoryDbContext, Guid>);

    private static void AssertGrainDirectoryFactoryRegistered(IServiceProvider services, GuidProvider provider)
    {
        if (provider is GuidProvider.MySql)
        {
            Assert.NotNull(services.GetRequiredService<IDbContextFactory<MySqlGrainDirectoryDbContext>>());
        }
        else
        {
            Assert.NotNull(services.GetRequiredService<IDbContextFactory<PostgreSqlGrainDirectoryDbContext>>());
        }
    }

    private static IServiceCollection AddGrainStorage(
        IServiceCollection services,
        GuidProvider provider,
        string name) =>
        provider is GuidProvider.MySql
            ? Orleans.Persistence.MySqlHostingExtensions.AddEntityFrameworkCoreMySqlGrainStorage(services, name)
            : Orleans.Persistence.PostgreSqlHostingExtensions.AddEntityFrameworkCorePostgreSqlGrainStorage(services, name);

    private static ISiloBuilder AddGrainStorage(
        ISiloBuilder builder,
        GuidProvider provider,
        string name,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        provider is GuidProvider.MySql
            ? Orleans.Persistence.MySqlHostingExtensions.AddEntityFrameworkCoreMySqlGrainStorage(
                builder,
                name,
                configureDatabase)
            : Orleans.Persistence.PostgreSqlHostingExtensions.AddEntityFrameworkCorePostgreSqlGrainStorage(
                builder,
                name,
                configureDatabase);

    private static void RegisterGrainStateFactory(
        IServiceCollection services,
        GuidProvider provider,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        if (provider is GuidProvider.MySql)
        {
            services.AddPooledDbContextFactory<MySqlGrainStateDbContext>(configureDatabase);
        }
        else
        {
            services.AddPooledDbContextFactory<PostgreSqlGrainStateDbContext>(configureDatabase);
        }
    }

    private static Type GetGrainStorageImplementationType(GuidProvider provider) =>
        provider is GuidProvider.MySql
            ? typeof(EFGrainStorage<MySqlGrainStateDbContext, Guid>)
            : typeof(EFGrainStorage<PostgreSqlGrainStateDbContext, Guid>);

    private static void AssertGrainStateFactoryRegistered(IServiceProvider services, GuidProvider provider)
    {
        if (provider is GuidProvider.MySql)
        {
            Assert.NotNull(services.GetRequiredService<IDbContextFactory<MySqlGrainStateDbContext>>());
        }
        else
        {
            Assert.NotNull(services.GetRequiredService<IDbContextFactory<PostgreSqlGrainStateDbContext>>());
        }
    }

    private static IServiceCollection UseReminderService(IServiceCollection services, GuidProvider provider) =>
        provider is GuidProvider.MySql
            ? Orleans.Reminders.MySqlHostingExtensions.UseEntityFrameworkCoreMySqlReminderService(services)
            : Orleans.Reminders.PostgreSqlHostingExtensions.UseEntityFrameworkCorePostgreSqlReminderService(services);

    private static ISiloBuilder UseReminderService(
        ISiloBuilder builder,
        GuidProvider provider,
        Action<DbContextOptionsBuilder> configureDatabase) =>
        provider is GuidProvider.MySql
            ? Orleans.Reminders.MySqlHostingExtensions.UseEntityFrameworkCoreMySqlReminderService(
                builder,
                configureDatabase)
            : Orleans.Reminders.PostgreSqlHostingExtensions.UseEntityFrameworkCorePostgreSqlReminderService(
                builder,
                configureDatabase);

    private static void RegisterReminderFactory(
        IServiceCollection services,
        GuidProvider provider,
        Action<DbContextOptionsBuilder> configureDatabase)
    {
        if (provider is GuidProvider.MySql)
        {
            services.AddPooledDbContextFactory<MySqlReminderDbContext>(configureDatabase);
        }
        else
        {
            services.AddPooledDbContextFactory<PostgreSqlReminderDbContext>(configureDatabase);
        }
    }

    private static Type GetReminderTableImplementationType(GuidProvider provider) =>
        provider is GuidProvider.MySql
            ? typeof(EFReminderTable<MySqlReminderDbContext, Guid>)
            : typeof(EFReminderTable<PostgreSqlReminderDbContext, Guid>);

    private static void AssertReminderFactoryRegistered(IServiceProvider services, GuidProvider provider)
    {
        if (provider is GuidProvider.MySql)
        {
            Assert.NotNull(services.GetRequiredService<IDbContextFactory<MySqlReminderDbContext>>());
        }
        else
        {
            Assert.NotNull(services.GetRequiredService<IDbContextFactory<PostgreSqlReminderDbContext>>());
        }
    }

    public enum GuidProvider
    {
        MySql,
        PostgreSql
    }
}
