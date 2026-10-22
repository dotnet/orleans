using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Serialization;
using Xunit;

#pragma warning disable ORLEANSEXP005

namespace NonSilo.Tests.DurableJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
public class DurableJobsOptionsTests
{
    [Fact]
    public void JournalProviderSelection_DefaultsToStandardProviderWithNoDrains()
    {
        var options = new DurableJobsOptions();

        Assert.Equal(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, options.ActiveProviderName);
        Assert.Empty(options.DrainingProviderNames);
        Assert.Equal(new[] { ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME }, DurableJobsJournalProviders.GetProviderNames(options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void JournalProviderSelection_RejectsBlankWriteOrDrainNames(string? name)
    {
        var write = new DurableJobsOptions { ActiveProviderName = name! };
        var writeFailure = Assert.Throws<OrleansConfigurationException>(() => DurableJobsJournalProviders.GetProviderNames(write));
        Assert.Contains(nameof(DurableJobsOptions.ActiveProviderName), writeFailure.Message);
        var drain = new DurableJobsOptions();
        drain.DrainingProviderNames.Add(name!);

        var drainFailure = Assert.Throws<OrleansConfigurationException>(() => DurableJobsJournalProviders.GetProviderNames(drain));

        Assert.Contains(nameof(DurableJobsOptions.DrainingProviderNames), drainFailure.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JournalProviderSelection_RejectsWriteDrainOverlapAndDuplicateDrains(bool duplicatesWrite)
    {
        var options = new DurableJobsOptions { ActiveProviderName = "current" };
        options.DrainingProviderNames.Add("old");
        options.DrainingProviderNames.Add(duplicatesWrite ? "current" : "old");

        var exception = Assert.Throws<OrleansConfigurationException>(() => DurableJobsJournalProviders.GetProviderNames(options));

        Assert.Contains(duplicatesWrite ? "'current'" : "'old'", exception.Message);
        Assert.Contains("more than once", exception.Message);
    }

    [Fact]
    public void UseJournaledDurableJobs_SelectsExplicitProviderIndependentlyOfDefaultGrainJournalingAndFreezesBindings()
    {
        var builder = CreateSelectionBuilder();
        builder.AddVolatileJournalStorage()
            .AddVolatileJournalStorage("current")
            .AddVolatileJournalStorage("old")
            .UseJournaledDurableJobs(options =>
            {
                options.ActiveProviderName = "current";
                options.DrainingProviderNames.Add("old");
            });
        using var services = builder.Services.BuildServiceProvider();

        new DurableJobsJournalingConfigurationValidator(services).ValidateConfiguration();
        var manager = Assert.IsType<JournaledJobShardManager>(services.GetRequiredService<JobShardManager>());
        var providers = services.GetRequiredService<DurableJobsJournalProviders>();
        Assert.Equal("current", manager.WriteProvider.Name);
        Assert.Equal(new[] { "current", "old" }, providers.Providers.Select(provider => provider.Name));
        Assert.Same(services.GetRequiredKeyedService<IJournalStorageProvider>("current"), manager.WriteProvider.Storage);
        Assert.Same(services.GetRequiredKeyedService<IJournaledStateManagerFactory>("current"), manager.WriteProvider.Factory);
        Assert.NotSame(services.GetRequiredService<IJournalStorageProvider>(), manager.WriteProvider.Storage);
        Assert.Same(services.GetRequiredKeyedService<IJournalStorageProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME),
            services.GetRequiredService<IJournalStorageProvider>());
        Assert.IsType<DurableJobsStorageInspector>(services.GetRequiredService<IDurableJobsStorageInspector>());

        var options = services.GetRequiredService<IOptions<DurableJobsOptions>>().Value;
        options.ActiveProviderName = "old";
        options.DrainingProviderNames.Clear();
        Assert.Equal("current", manager.WriteProvider.Name);
        Assert.Equal(new[] { "current", "old" }, providers.Providers.Select(provider => provider.Name));
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("catalog")]
    [InlineData("factory")]
    public void UseJournaledDurableJobs_MissingSelectedCollaboratorNamesProviderInConfigurationFailure(string missing)
    {
        var builder = CreateSelectionBuilder();
        builder.AddVolatileJournalStorage("current").AddVolatileJournalStorage("old")
            .UseJournaledDurableJobs(options =>
            {
                options.ActiveProviderName = "current";
                options.DrainingProviderNames.Add("old");
            });
        var missingType = missing switch
        {
            "storage" => typeof(IJournalStorageProvider),
            "catalog" => typeof(IJournalStorageCatalog),
            _ => typeof(IJournaledStateManagerFactory)
        };
        var descriptor = Assert.Single(builder.Services,
            service => service.IsKeyedService && Equals(service.ServiceKey, "old") && service.ServiceType == missingType);
        builder.Services.Remove(descriptor);
        using var services = builder.Services.BuildServiceProvider();

        var exception = Assert.Throws<OrleansConfigurationException>(
            new DurableJobsJournalingConfigurationValidator(services).ValidateConfiguration);

        Assert.Contains("'old'", exception.Message);
        Assert.Contains("storage, catalog, and state-manager factory", exception.Message);
    }

    [Fact]
    public void UseJournaledDurableJobs_RejectsNonJournaledShardManagerEvenWithValidJournalProviders()
    {
        var builder = CreateSelectionBuilder();
        builder.AddVolatileJournalStorage().UseJournaledDurableJobs();
        var nonJournaled = Substitute.For<JobShardManager>(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5120), 0));
        builder.Services.AddSingleton(nonJournaled);
        using var services = builder.Services.BuildServiceProvider();

        var exception = Assert.Throws<OrleansConfigurationException>(
            new DurableJobsJournalingConfigurationValidator(services).ValidateConfiguration);

        Assert.Contains("requires the journaled shard manager", exception.Message);
        Assert.Same(nonJournaled, services.GetRequiredService<JobShardManager>());
    }

    private static SelectionSiloBuilder CreateSelectionBuilder()
    {
        var builder = new SelectionSiloBuilder();
        builder.Services.AddLogging();
        builder.Services.AddSerializer();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        var details = Substitute.For<ILocalSiloDetails>();
        details.SiloAddress.Returns(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5120), 0));
        builder.Services.AddSingleton(details);
        builder.Services.AddSingleton(Substitute.For<IClusterMembershipService>());
        builder.Services.AddSingleton(DurableJobsInstruments.CreateForDirectConstruction());
        return builder;
    }

    private sealed class SelectionSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    [Fact]
    public void DiscoveryTimingDefaults()
    {
        var options = new DurableJobsOptions();

        Assert.Equal(TimeSpan.FromMinutes(10), options.ShardLoadLookaheadPeriod);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ShardCheckInterval);
        CreateValidator(options).ValidateConfiguration();
    }

    [Fact]
    public void ValidateConfiguration_NegativeLookahead_Throws()
    {
        var options = new DurableJobsOptions { ShardLoadLookaheadPeriod = TimeSpan.FromTicks(-1) };

        var exception = Assert.Throws<OrleansConfigurationException>(CreateValidator(options).ValidateConfiguration);

        Assert.Contains(nameof(DurableJobsOptions.ShardLoadLookaheadPeriod), exception.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(0.9999)]
    [InlineData(4294967295)]
    public void ValidateConfiguration_InvalidCheckInterval_Throws(double milliseconds)
    {
        var options = new DurableJobsOptions { ShardCheckInterval = TimeSpan.FromMilliseconds(milliseconds) };

        var exception = Assert.Throws<OrleansConfigurationException>(CreateValidator(options).ValidateConfiguration);

        Assert.Contains(nameof(DurableJobsOptions.ShardCheckInterval), exception.Message);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(23, 120000)]
    [InlineData(10, 4294967294)]
    public void ValidateConfiguration_ValidDiscoveryTimings_AcceptsValues(int lookaheadMinutes, double intervalMilliseconds)
    {
        var options = new DurableJobsOptions
        {
            ShardLoadLookaheadPeriod = TimeSpan.FromMinutes(lookaheadMinutes),
            ShardCheckInterval = TimeSpan.FromMilliseconds(intervalMilliseconds)
        };

        CreateValidator(options).ValidateConfiguration();
        using var timer = new PeriodicTimer(options.ShardCheckInterval);
        Assert.Equal(options.ShardCheckInterval, timer.Period);
        Assert.Equal(TimeSpan.FromMinutes(lookaheadMinutes), options.ShardLoadLookaheadPeriod);
    }

    private static DurableJobsOptionsValidator CreateValidator(DurableJobsOptions options) =>
        new(NullLogger<DurableJobsOptionsValidator>.Instance, Options.Create(options));
}
