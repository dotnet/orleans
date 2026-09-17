using System.Reflection;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Journaling.Json;
using Orleans.Providers;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class AzureTableStorageJournalingProviderBuilderTests
{
    private const string ProviderName = "provider-name";
    private static readonly Uri NamedServiceUri = new("https://named.table.example/");
    private static readonly Uri DirectServiceUri = new("https://direct.table.example/");

    [Fact]
    public void Configure_EmptyValues_PreservesDefaultsAndRegistersStorage()
    {
        var builder = CreateBuilder(
            ("Provider:TableName", ""),
            ("Provider:ServiceKey", ""),
            ("Provider:ConnectionName", ""),
            ("Provider:ConnectionString", ""),
            ("Provider:JournalFormatKey", " \t"));

        Configure(builder, "Provider");

        using var services = builder.Services.BuildServiceProvider();
        var storageOptions = GetStorageOptions(services);
        var managerOptions = services.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value;
        Assert.Equal(AzureTableJournalStorageOptions.DEFAULT_TABLE_NAME, storageOptions.TableName);
        Assert.Null(storageOptions.TableServiceClient);
        Assert.Null(storageOptions.CreateClient);
        Assert.Equal(JsonLinesJournalFormat.JournalFormatKey, managerOptions.JournalFormatKey);
        AssertNamedRegistration<IJournalStorageProvider>(builder.Services);
    }

    [Fact]
    public void Configure_TableNameAndJournalFormatKey_AppliesBothOptions()
    {
        var builder = CreateBuilder(
            ("Provider:TableName", "custom-journal"),
            ("Provider:JournalFormatKey", "orleans-binary"));

        Configure(builder, "Provider");

        using var services = builder.Services.BuildServiceProvider();
        Assert.Equal(
            "custom-journal",
            GetStorageOptions(services).TableName);
        Assert.Equal(
            AzureTableJournalStorageOptions.DEFAULT_TABLE_NAME,
            services.GetRequiredService<IOptions<AzureTableJournalStorageOptions>>().Value.TableName);
        Assert.Equal(
            "orleans-binary",
            services.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value.JournalFormatKey);
    }

    [Fact]
    public void Configure_OperationalOptions_BindsAllSupportedValues()
    {
        var builder = CreateBuilder(
            ("Provider:DeleteOldGenerations", "false"),
            ("Provider:CompactionRowCountThreshold", "123"),
            ("Provider:CompactionSizeThreshold", "456"),
            ("Provider:MaxMetadataOnlyConflictRetries", "7"),
            ("Provider:MetadataOnlyConflictInitialBackoff", "00:00:00.025"),
            ("Provider:MetadataOnlyConflictMaxBackoff", "00:00:00.500"));

        Configure(builder, "Provider");

        using var services = builder.Services.BuildServiceProvider();
        var options = GetStorageOptions(services);
        Assert.False(options.DeleteOldGenerations);
        Assert.Equal(123, options.CompactionRowCountThreshold);
        Assert.Equal(456, options.CompactionSizeThreshold);
        Assert.Equal(7, options.MaxMetadataOnlyConflictRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(25), options.MetadataOnlyConflictInitialBackoff);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.MetadataOnlyConflictMaxBackoff);
    }

    [Fact]
    public async Task Configure_ServiceKey_TakesPrecedenceOverConnectionSettings()
    {
        const string serviceKey = "shared-tables";
        var keyedClient = new TableServiceClient(
            new Uri("https://keyed.table.example/"),
            new TableSharedKeyCredential("keyed", Convert.ToBase64String(new byte[32])));
        var builder = CreateBuilder(
            ("Provider:ServiceKey", serviceKey),
            ("Provider:ConnectionName", "named"),
            ("Provider:ConnectionString", CreateConnectionString("direct", DirectServiceUri)),
            ("ConnectionStrings:named", CreateConnectionString("named", NamedServiceUri)));
        builder.Services.AddKeyedSingleton(serviceKey, keyedClient);

        Configure(builder, "Provider");

        using var services = builder.Services.BuildServiceProvider();
        var options = GetStorageOptions(services);
        Assert.Same(keyedClient, options.TableServiceClient);
        Assert.Same(
            keyedClient,
            await options.CreateClient!(CancellationToken.None));
    }

    [Fact]
    public void Configure_ConnectionName_UsesRootConnectionString()
    {
        var builder = CreateBuilder(
            ("Provider:ConnectionName", "named"),
            ("ConnectionStrings:named", CreateConnectionString("named", NamedServiceUri)));

        Configure(builder, "Provider");

        using var services = builder.Services.BuildServiceProvider();
        var client = GetStorageOptions(services).TableServiceClient;
        Assert.NotNull(client);
        Assert.Equal("named", client.AccountName);
        Assert.Equal(NamedServiceUri, client.Uri);
    }

    [Fact]
    public void Configure_DirectConnectionString_TakesPrecedenceOverConnectionName()
    {
        var builder = CreateBuilder(
            ("Provider:ConnectionName", "named"),
            ("Provider:ConnectionString", CreateConnectionString("direct", DirectServiceUri)),
            ("ConnectionStrings:named", CreateConnectionString("named", NamedServiceUri)));

        Configure(builder, "Provider");

        using var services = builder.Services.BuildServiceProvider();
        var client = GetStorageOptions(services).TableServiceClient;
        Assert.NotNull(client);
        Assert.Equal("direct", client.AccountName);
        Assert.Equal(DirectServiceUri, client.Uri);
    }

    [Fact]
    public void Configure_AbsoluteServiceUri_CreatesClientForThatEndpoint()
    {
        var builder = CreateBuilder(
            ("Provider:ConnectionString", "https://account.table.example/?sv=2026-01-01&ss=t&sig=signature"));

        Configure(builder, "Provider");

        using var services = builder.Services.BuildServiceProvider();
        var client = GetStorageOptions(services).TableServiceClient;
        Assert.NotNull(client);
        Assert.Equal("account", client.AccountName);
        Assert.Equal(new Uri("https://account.table.example/"), client.Uri);
    }

    [Fact]
    public void Configure_RepeatedCallsKeepRegistrationsSingleAndApplyNonEmptyOptionsInOrder()
    {
        var builder = CreateBuilder(
            ("First:TableName", "first"),
            ("First:JournalFormatKey", "orleans-binary"),
            ("Second:TableName", "second"),
            ("Second:JournalFormatKey", " "));

        Configure(builder, "First");
        Configure(builder, "Second");

        AssertNamedRegistration<IJournalStorageProvider>(builder.Services);
        AssertNamedRegistration<IJournalStorageCatalog>(builder.Services);
        AssertNamedRegistration<IJournaledStateManagerFactory>(builder.Services);
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(ILifecycleParticipant<ISiloLifecycle>));

        using var services = builder.Services.BuildServiceProvider();
        Assert.Equal(
            "second",
            GetStorageOptions(services).TableName);
        Assert.Equal(
            "orleans-binary",
            services.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value.JournalFormatKey);
    }

    [Theory]
    [InlineData("AzureTableStorage", typeof(AzureTableStorageJournalingProviderBuilder))]
    [InlineData("AzureBlobStorage", typeof(AzureBlobStorageJournalingProviderBuilder))]
    public void Assembly_RegistersExpectedProviderMetadata(string providerType, Type builderType)
    {
        var attribute = typeof(AzureTableStorageHostingExtensions)
            .Assembly
            .GetCustomAttributes<RegisterProviderAttribute>()
            .Single(candidate => candidate.Type == builderType);

        Assert.Equal(providerType, attribute.Name);
        Assert.Equal("Journaling", attribute.Kind);
        Assert.Equal("Silo", attribute.Target);
        Assert.Equal(builderType, attribute.Type);
    }

    private static void Configure(TestSiloBuilder builder, string sectionName)
        => new AzureTableStorageJournalingProviderBuilder()
            .Configure(builder, ProviderName, builder.Configuration.GetSection(sectionName));

    private static AzureTableJournalStorageOptions GetStorageOptions(IServiceProvider services)
        => services.GetRequiredService<IOptionsMonitor<AzureTableJournalStorageOptions>>().Get(ProviderName);

    private static void AssertNamedRegistration<TService>(IServiceCollection services)
        => Assert.Single(services, descriptor => descriptor.ServiceType == typeof(TService)
            && descriptor.IsKeyedService && Equals(descriptor.ServiceKey, ProviderName));

    private static TestSiloBuilder CreateBuilder(params (string Key, string? Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => pair.Value))
            .Build();
        return new TestSiloBuilder(configuration);
    }

    private static string CreateConnectionString(string accountName, Uri serviceUri)
        => $"DefaultEndpointsProtocol=https;AccountName={accountName};AccountKey={Convert.ToBase64String(new byte[32])};TableEndpoint={serviceUri}";

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public TestSiloBuilder(IConfiguration configuration)
        {
            Configuration = configuration;
            Services.AddSingleton(configuration);
            Services.AddSingleton<IConfiguration>(configuration);
        }

        public IConfiguration Configuration { get; }

        public IServiceCollection Services { get; } = new ServiceCollection();
    }
}
