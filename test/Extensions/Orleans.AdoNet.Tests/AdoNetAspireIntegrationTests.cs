using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Orleans;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory.AdoNet;
using Orleans.Hosting;
using Orleans.Runtime;
using TestExtensions;

namespace UnitTests.AdoNet;

[TestCategory("AdoNet")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Persistence")]
public sealed class AdoNetAspireIntegrationTests
{
    [Theory]
    [InlineData(AdoNetAspireDatabase.SqlServer, "SqlServerDatabase", "Microsoft.Data.SqlClient")]
    [InlineData(AdoNetAspireDatabase.PostgreSql, "PostgresDatabase", "Npgsql")]
    [InlineData(AdoNetAspireDatabase.MySql, "MySqlDatabase", "MySql.Data.MySqlClient")]
    [InlineData(AdoNetAspireDatabase.Oracle, "OracleDatabase", "Oracle.DataAccess.Client")]
    public async Task GeneratedConfiguration_ActivatesAllSupportedCapabilities(
        AdoNetAspireDatabase databaseType,
        string expectedProviderType,
        string expectedInvariant)
    {
        var capabilities = GetSupportedCapabilities(databaseType);
        using var generated = await AdoNetAspireTestConfiguration.CreateAsync(databaseType, capabilities);
        var normalizedConnectionStringKey = $"ConnectionStrings:{generated.DatabaseName}";

        var connectionString = generated.HostConfiguration.GetConnectionString(generated.DatabaseName);
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        Assert.Equal(connectionString, generated.HostConfiguration[normalizedConnectionStringKey]);
        Assert.Contains(generated.DatabaseName, connectionString, StringComparison.Ordinal);

        foreach (var capability in capabilities)
        {
            var capabilityPath = GetCapabilityPath(generated, capability);
            var normalizedPrefix = $"Orleans:{capabilityPath}";

            Assert.Equal(expectedProviderType, generated.HostConfiguration[$"{normalizedPrefix}:ProviderType"]);
            Assert.Equal(generated.DatabaseName, generated.HostConfiguration[$"{normalizedPrefix}:ServiceKey"]);
        }

        using var siloHost = CreateSiloHost(generated.HostConfiguration);
        AssertSiloOptions(siloHost.Services, generated, expectedInvariant, connectionString);

        using var clientHost = CreateClientHost(generated);
        var clientOptions = clientHost.Services.GetRequiredService<IOptions<AdoNetClusteringClientOptions>>().Value;
        Assert.Equal(expectedInvariant, clientOptions.Invariant);
        Assert.Equal(connectionString, clientOptions.ConnectionString);

        if (capabilities.Contains(AdoNetAspireCapability.Streaming))
        {
            var streamOptions = clientHost.Services
                .GetRequiredService<IOptionsMonitor<AdoNetStreamOptions>>()
                .Get(generated.ProviderName);
            Assert.Equal(expectedInvariant, streamOptions.Invariant);
            Assert.Equal(connectionString, streamOptions.ConnectionString);
        }
    }

    [Fact]
    public async Task GeneratedConfiguration_ExplicitAdoNetAndInvariant_ProducesEquivalentOverrideShape()
    {
        const string invariant = "Microsoft.Data.SqlClient";
        using var generated = await AdoNetAspireTestConfiguration.CreateAsync(
            AdoNetAspireDatabase.SqlServer,
            [AdoNetAspireCapability.Clustering],
            invariant);

        Assert.Equal("AdoNet", generated.HostConfiguration["Orleans:Clustering:ProviderType"]);
        Assert.Equal(invariant, generated.HostConfiguration["Orleans:Clustering:Invariant"]);
        Assert.Equal(generated.DatabaseName, generated.HostConfiguration["Orleans:Clustering:ServiceKey"]);
        Assert.False(string.IsNullOrWhiteSpace(
            generated.HostConfiguration.GetConnectionString(generated.DatabaseName)));
    }

    private static void AssertSiloOptions(
        IServiceProvider services,
        AdoNetAspireTestConfiguration.GeneratedConfiguration generated,
        string expectedInvariant,
        string connectionString)
    {
        var clustering = services.GetRequiredService<IOptions<AdoNetClusteringSiloOptions>>().Value;
        Assert.Equal(expectedInvariant, clustering.Invariant);
        Assert.Equal(connectionString, clustering.ConnectionString);

        var storage = services.GetRequiredService<IOptionsMonitor<AdoNetGrainStorageOptions>>().Get(generated.ProviderName);
        Assert.Equal(expectedInvariant, storage.Invariant);
        Assert.Equal(connectionString, storage.ConnectionString);

        var reminders = services.GetRequiredService<IOptions<AdoNetReminderTableOptions>>().Value;
        Assert.Equal(expectedInvariant, reminders.Invariant);
        Assert.Equal(connectionString, reminders.ConnectionString);

        if (generated.Capabilities.Contains(AdoNetAspireCapability.GrainDirectory))
        {
            var directory = services
                .GetRequiredService<IOptionsMonitor<AdoNetGrainDirectoryOptions>>()
                .Get(generated.ProviderName);
            Assert.Equal(expectedInvariant, directory.Invariant);
            Assert.Equal(connectionString, directory.ConnectionString);
        }

        if (generated.Capabilities.Contains(AdoNetAspireCapability.Streaming))
        {
            var streaming = services
                .GetRequiredService<IOptionsMonitor<AdoNetStreamOptions>>()
                .Get(generated.ProviderName);
            Assert.Equal(expectedInvariant, streaming.Invariant);
            Assert.Equal(connectionString, streaming.ConnectionString);
        }
    }

    [Fact]
    public async Task MissingConnectionReference_FailsStreamingValidation()
    {
        await using var builder = DistributedApplicationTestingBuilder.Create();
        var orleans = builder.AddOrleans("cluster")
            .WithDevelopmentClustering()
            .WithStreaming("streams", new MissingConnectionProviderConfiguration());
        var silo = builder.AddContainer("silo", "unused").WithReference(orleans);
        await using var app = await builder.BuildAsync(TestContext.Current.CancellationToken);
        var configuration = await AspireResourceConfiguration.CreateAsync(
            silo.Resource,
            app.Services,
            include: static key => key.StartsWith("Orleans__Streaming__", StringComparison.Ordinal));
        using var host = CreateSiloHost(configuration);
        var validator = host.Services
            .GetServices<IConfigurationValidator>()
            .OfType<AdoNetStreamOptionsValidator>()
            .Single();

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains("configure exactly one of ConnectionString or DataSource", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-an-integer")]
    [InlineData("0")]
    [InlineData("-1")]
    public void InvalidPartitionCount_FailsWithConfigurationPath(string partitionCount)
    {
        const string providerName = "streams";
        var sectionPath = $"Orleans:Streaming:{providerName}";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{sectionPath}:ProviderType"] = "AdoNet",
                [$"{sectionPath}:Invariant"] = "Microsoft.Data.SqlClient",
                [$"{sectionPath}:ConnectionString"] = "Server=localhost;Database=orleans",
                [$"{sectionPath}:PartitionCount"] = partitionCount,
            })
            .Build();

        var exception = Assert.Throws<OrleansConfigurationException>(() => CreateSiloHost(configuration));

        Assert.Contains(sectionPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("PartitionCount", exception.Message, StringComparison.Ordinal);
        Assert.Contains("positive integer", exception.Message, StringComparison.Ordinal);
    }

    private static IHost CreateSiloHost(IConfiguration configuration)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.UseOrleans();
        return builder.Build();
    }

    private static IHost CreateClientHost(AdoNetAspireTestConfiguration.GeneratedConfiguration generated)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(generated.ClientConfiguration);
        builder.UseOrleansClient();
        return builder.Build();
    }

    private sealed class MissingConnectionProviderConfiguration : IProviderConfiguration
    {
        public void ConfigureResource<T>(
            IResourceBuilder<T> resourceBuilder,
            string configurationSectionPath)
            where T : IResourceWithEnvironment
            => resourceBuilder.WithEnvironment(
                $"Orleans__{configurationSectionPath.Replace(":", "__", StringComparison.Ordinal)}__ProviderType",
                "SqlServerDatabase");
    }

    private static IReadOnlyList<AdoNetAspireCapability> GetSupportedCapabilities(AdoNetAspireDatabase databaseType)
        => databaseType == AdoNetAspireDatabase.Oracle
            ?
            [
                AdoNetAspireCapability.Clustering,
                AdoNetAspireCapability.GrainStorage,
                AdoNetAspireCapability.Reminders,
            ]
            :
            [
                AdoNetAspireCapability.Clustering,
                AdoNetAspireCapability.GrainStorage,
                AdoNetAspireCapability.Reminders,
                AdoNetAspireCapability.GrainDirectory,
                AdoNetAspireCapability.Streaming,
            ];

    private static string GetCapabilityPath(
        AdoNetAspireTestConfiguration.GeneratedConfiguration generated,
        AdoNetAspireCapability capability)
        => capability switch
        {
            AdoNetAspireCapability.Clustering => "Clustering",
            AdoNetAspireCapability.GrainStorage => $"GrainStorage:{generated.ProviderName}",
            AdoNetAspireCapability.Reminders => "Reminders",
            AdoNetAspireCapability.GrainDirectory => $"GrainDirectory:{generated.ProviderName}",
            AdoNetAspireCapability.Streaming => $"Streaming:{generated.ProviderName}",
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null),
        };

}
