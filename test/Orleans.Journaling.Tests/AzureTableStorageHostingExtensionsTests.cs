using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
public sealed class AzureTableStorageHostingExtensionsTests
{
    [Fact]
    public void AddAzureTableJournalStorage_WithoutConfiguration_ReturnsBuilderAndUsesDefaults()
    {
        var builder = CreateBuilder();

        var result = builder.AddAzureTableJournalStorage();

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<AzureTableJournalStorageOptions>>().Value;
        Assert.Same(builder, result);
        Assert.Equal(AzureTableJournalStorageOptions.DEFAULT_TABLE_NAME, options.TableName);
        Assert.True(options.DeleteOldGenerations);
        Assert.Equal(AzureTableJournalStorageOptions.DEFAULT_COMPACTION_ROW_COUNT_THRESHOLD, options.CompactionRowCountThreshold);
        Assert.Null(options.TableServiceClient);
        Assert.Null(options.CreateClient);
    }

    [Fact]
    public void AddAzureTableJournalStorage_DefaultOptionsComposeUnnamedDelegatesAndConfigureAllExactlyOnce()
    {
        var builder = CreateBuilder();
        var invocations = new List<string>();
        builder.Services.Configure<AzureTableJournalStorageOptions>(options =>
        {
            invocations.Add("unnamed-before");
            options.TableName = "before";
        });
        builder.Services.ConfigureAll<AzureTableJournalStorageOptions>(options =>
        {
            invocations.Add("all");
            options.CompactionRowCountThreshold += 100;
        });
        builder.AddAzureTableJournalStorage(options =>
        {
            invocations.Add("default");
            options.TableName = "default";
        });
        builder.Services.Configure<AzureTableJournalStorageOptions>(options =>
        {
            invocations.Add("unnamed-after");
            options.TableName = "after";
        });
        builder.AddAzureTableJournalStorage("other", options =>
        {
            invocations.Add("other");
            options.TableName = "other";
        });
        using var services = builder.Services.BuildServiceProvider();

        var defaultOptions = services.GetRequiredService<IOptions<AzureTableJournalStorageOptions>>().Value;

        Assert.Equal("after", defaultOptions.TableName);
        Assert.Equal(AzureTableJournalStorageOptions.DEFAULT_COMPACTION_ROW_COUNT_THRESHOLD + 100,
            defaultOptions.CompactionRowCountThreshold);
        Assert.Equal(["unnamed-before", "all", "default", "unnamed-after"], invocations);
        var namedOptions = services.GetRequiredService<IOptionsMonitor<AzureTableJournalStorageOptions>>().Get("other");
        Assert.Equal("other", namedOptions.TableName);
        Assert.Equal(AzureTableJournalStorageOptions.DEFAULT_COMPACTION_ROW_COUNT_THRESHOLD + 100,
            namedOptions.CompactionRowCountThreshold);
        Assert.NotSame(defaultOptions, namedOptions);
        Assert.Same(defaultOptions, services.GetRequiredService<IOptions<AzureTableJournalStorageOptions>>().Value);
        Assert.Same(defaultOptions, services.GetJournalStorageOptions<AzureTableJournalStorageOptions>(
            ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME).Value);
        Assert.Same(namedOptions, services.GetJournalStorageOptions<AzureTableJournalStorageOptions>("other").Value);
        Assert.Equal(["unnamed-before", "all", "default", "unnamed-after", "all", "other"], invocations);
    }

    [Fact]
    public void AddAzureTableJournalStorage_RegistersProviderAliasesInstrumentsAndJournalingServices()
    {
        var builder = CreateBuilder();
        builder.AddAzureTableJournalStorage();

        Assert.Contains(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(AzureTableJournalStorageInstruments)
                && descriptor.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IJournaledStateManager)
                && descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(
            builder.Services,
            descriptor => descriptor.ServiceType == typeof(IJournaledStateManagerFactory)
                && descriptor.Lifetime == ServiceLifetime.Singleton);

        using var services = builder.Services.BuildServiceProvider();
        var provider = services.GetRequiredService<AzureTableJournalStorageProvider>();

        Assert.Same(provider, services.GetRequiredService<IJournalStorageProvider>());
        Assert.Same(provider, services.GetRequiredService<IJournalStorageCatalog>());
        Assert.Same(provider, services.GetRequiredService<ILifecycleParticipant<ISiloLifecycle>>());
        Assert.Same(
            services.GetRequiredService<AzureTableJournalStorageInstruments>(),
            services.GetRequiredService<AzureTableJournalStorageInstruments>());
    }

    [Fact]
    public void AddAzureTableJournalStorage_RepeatedCallsKeepRegistrationsSingleAndApplyOptionsInOrder()
    {
        var builder = CreateBuilder();
        var invocations = new List<string>();

        builder.AddAzureTableJournalStorage(options =>
        {
            invocations.Add("first");
            options.TableName = "first";
            options.DeleteOldGenerations = false;
        });
        builder.AddAzureTableJournalStorage(options =>
        {
            invocations.Add("second");
            options.TableName = "second";
            options.CompactionRowCountThreshold = 321;
        });

        AssertSingleRegistration<AzureTableJournalStorageProvider>(builder.Services);
        AssertSingleRegistration<AzureTableJournalStorageInstruments>(builder.Services);
        AssertSingleRegistration<IJournalStorageProvider>(builder.Services);
        AssertSingleRegistration<IJournalStorageCatalog>(builder.Services);
        AssertSingleRegistration<ILifecycleParticipant<ISiloLifecycle>>(builder.Services);
        AssertSingleRegistration<IJournaledStateManager>(builder.Services);
        AssertSingleRegistration<IJournaledStateManagerFactory>(builder.Services);
        foreach (var serviceType in new[] { typeof(IJournalStorageProvider), typeof(IJournalStorageCatalog), typeof(IJournaledStateManagerFactory) })
        {
            Assert.Single(builder.Services, descriptor => descriptor.IsKeyedService
                && Equals(descriptor.ServiceKey, ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)
                && descriptor.ServiceType == serviceType);
        }

        using var services = builder.Services.BuildServiceProvider();
        var optionsMonitor = services.GetRequiredService<IOptions<AzureTableJournalStorageOptions>>();
        var options = optionsMonitor.Value;

        Assert.Equal(["first", "second"], invocations);
        Assert.Equal("second", options.TableName);
        Assert.False(options.DeleteOldGenerations);
        Assert.Equal(321, options.CompactionRowCountThreshold);
        Assert.Same(options, optionsMonitor.Value);
        Assert.Equal(["first", "second"], invocations);
    }

    private static TestSiloBuilder CreateBuilder()
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddLogging();
        builder.Services.AddMetrics();
        builder.Services.AddSingleton<OrleansInstruments>();
        return builder;
    }

    private static void AssertSingleRegistration<TService>(IServiceCollection services)
        => Assert.Single(services, descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(TService));

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
