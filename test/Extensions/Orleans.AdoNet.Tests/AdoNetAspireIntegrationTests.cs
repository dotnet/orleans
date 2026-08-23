using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory.AdoNet;
using Orleans.Hosting;
using Orleans.Runtime;

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
        var rawConnectionStringKey = $"ConnectionStrings__{generated.DatabaseName}";
        var normalizedConnectionStringKey = $"ConnectionStrings:{generated.DatabaseName}";

        Assert.True(generated.RawEnvironment.TryGetValue(rawConnectionStringKey, out var connectionString));
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        Assert.Equal(connectionString, generated.HostConfiguration[normalizedConnectionStringKey]);
        Assert.Contains(generated.DatabaseName, connectionString, StringComparison.Ordinal);

        foreach (var capability in capabilities)
        {
            var capabilityPath = GetCapabilityPath(generated, capability);
            var rawPrefix = $"Orleans__{capabilityPath.Replace(":", "__", StringComparison.Ordinal)}";
            var normalizedPrefix = $"Orleans:{capabilityPath}";

            Assert.Equal(expectedProviderType, generated.RawEnvironment[$"{rawPrefix}__ProviderType"]);
            Assert.Equal(generated.DatabaseName, generated.RawEnvironment[$"{rawPrefix}__ServiceKey"]);
            Assert.Equal(expectedProviderType, generated.HostConfiguration[$"{normalizedPrefix}:ProviderType"]);
            Assert.Equal(generated.DatabaseName, generated.HostConfiguration[$"{normalizedPrefix}:ServiceKey"]);
            Assert.DoesNotContain($"{normalizedPrefix}:ProviderType", generated.RawEnvironment.Keys);
            Assert.Null(generated.HostConfiguration[$"{rawPrefix}__ProviderType"]);
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

        Assert.Equal("AdoNet", generated.RawEnvironment["Orleans__Clustering__ProviderType"]);
        Assert.Equal(invariant, generated.RawEnvironment["Orleans__Clustering__Invariant"]);
        Assert.Equal(generated.DatabaseName, generated.RawEnvironment["Orleans__Clustering__ServiceKey"]);
        Assert.Equal("AdoNet", generated.HostConfiguration["Orleans:Clustering:ProviderType"]);
        Assert.Equal(invariant, generated.HostConfiguration["Orleans:Clustering:Invariant"]);
        Assert.Equal(generated.DatabaseName, generated.HostConfiguration["Orleans:Clustering:ServiceKey"]);
        Assert.Equal(
            generated.RawEnvironment[$"ConnectionStrings__{generated.DatabaseName}"],
            generated.HostConfiguration[$"ConnectionStrings:{generated.DatabaseName}"]);
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
    public void MissingConnectionReference_FailsStreamingValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Orleans:Streaming:streams:ProviderType"] = "SqlServerDatabase",
            })
            .Build();
        using var host = CreateSiloHost(configuration);
        var validator = host.Services
            .GetServices<IConfigurationValidator>()
            .OfType<AdoNetStreamOptionsValidator>()
            .Single();

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains("configure exactly one of ConnectionString or DataSource", exception.Message, StringComparison.Ordinal);
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
        builder.Configuration.AddInMemoryCollection(
            generated.HostConfiguration.AsEnumerable().Where(static pair =>
                pair.Key.StartsWith("Orleans:Clustering", StringComparison.Ordinal)
                || pair.Key.StartsWith("Orleans:Streaming", StringComparison.Ordinal)
                || pair.Key.StartsWith("ConnectionStrings:", StringComparison.Ordinal)));
        builder.UseOrleansClient();
        return builder.Build();
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
