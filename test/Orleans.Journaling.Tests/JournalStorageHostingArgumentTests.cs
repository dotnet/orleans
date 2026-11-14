using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Providers;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestCategory("BVT")]
[TestArea("Journaling")]
public sealed class JournalStorageHostingArgumentTests
{
    [Fact]
    public void AddAzureBlobJournalStorage_NullBuilder_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => AzureBlobStorageHostingExtensions.AddAzureBlobJournalStorage(null!));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void AddAzureTableJournalStorage_NullBuilder_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => AzureTableStorageHostingExtensions.AddAzureTableJournalStorage(null!));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void AddAzureBlobJournalStorage_NullConfigure_UsesDefaultProvider()
    {
        var builder = CreateBuilder();

        Assert.Same(builder, builder.AddAzureBlobJournalStorage(null));

        AssertDefaultProvider<AzureBlobJournalStorageProvider>(builder);
    }

    [Fact]
    public void AddAzureTableJournalStorage_NullConfigure_UsesDefaultProvider()
    {
        var builder = CreateBuilder();

        Assert.Same(builder, builder.AddAzureTableJournalStorage(null));

        AssertDefaultProvider<AzureTableJournalStorageProvider>(builder);
    }

    [Fact]
    public void AddS3JournalStorage_NullConfigure_UsesDefaultProvider()
    {
        var builder = CreateBuilder();
        builder.Services.Configure<S3JournalStorageOptions>(options => options.BucketName = "journaling-tests");

        Assert.Same(builder, builder.AddS3JournalStorage(null));

        AssertDefaultProvider<S3JournalStorageProvider>(builder);
    }

    private static void AssertDefaultProvider<TProvider>(ISiloBuilder builder)
        where TProvider : class, IJournalStorageProvider
    {
        using var services = builder.Services.BuildServiceProvider();
        var provider = Assert.IsType<TProvider>(services.GetRequiredService<IJournalStorageProvider>());
        Assert.Same(provider, services.GetRequiredKeyedService<IJournalStorageProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
    }

    private static TestSiloBuilder CreateBuilder()
    {
        var builder = new TestSiloBuilder();
        builder.Services.AddLogging();
        builder.Services.AddMetrics();
        builder.Services.AddSingleton<OrleansInstruments>();
        return builder;
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
