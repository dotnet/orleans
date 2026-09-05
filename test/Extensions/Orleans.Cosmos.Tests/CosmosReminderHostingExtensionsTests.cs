using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Reminders.Cosmos;

namespace Tester.Cosmos.Reminders;

[TestCategory("Cosmos"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("Cosmos")]
[TestArea("Reminders")]
public class CosmosReminderHostingExtensionsTests
{
    [Fact]
    public void UseCosmosReminderService_NullBuilderWithOptionsCallback_Throws()
    {
        ISiloBuilder builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.UseCosmosReminderService((CosmosReminderTableOptions _) => { }));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void UseCosmosReminderService_NullOptionsCallbackWithBuilder_Throws()
    {
        var builder = new TestSiloBuilder();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.UseCosmosReminderService((Action<CosmosReminderTableOptions>)null!));

        Assert.Equal("configure", exception.ParamName);
        Assert.Empty(builder.Services);
    }

    [Fact]
    public void UseCosmosReminderService_NullBuilderWithOptionsBuilderCallback_Throws()
    {
        ISiloBuilder builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.UseCosmosReminderService((OptionsBuilder<CosmosReminderTableOptions> _) => { }));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void UseCosmosReminderService_NullOptionsBuilderCallbackWithBuilder_Throws()
    {
        var builder = new TestSiloBuilder();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.UseCosmosReminderService((Action<OptionsBuilder<CosmosReminderTableOptions>>)null!));

        Assert.Equal("configure", exception.ParamName);
        Assert.Empty(builder.Services);
    }

    [Fact]
    public void UseCosmosReminderService_NullOptionsCallback_ThrowsBeforeRegisteringServices()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.UseCosmosReminderService((Action<CosmosReminderTableOptions>)null!));

        Assert.Equal("configure", exception.ParamName);
        Assert.Empty(services);
    }

    [Fact]
    public void UseCosmosReminderService_NullServicesWithOptionsCallback_Throws()
    {
        IServiceCollection services = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.UseCosmosReminderService((CosmosReminderTableOptions _) => { }));

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void UseCosmosReminderService_NullOptionsBuilderCallback_ThrowsBeforeRegisteringServices()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.UseCosmosReminderService((Action<OptionsBuilder<CosmosReminderTableOptions>>)null!));

        Assert.Equal("configure", exception.ParamName);
        Assert.Empty(services);
    }

    [Fact]
    public void UseCosmosReminderService_NullServicesWithOptionsBuilderCallback_Throws()
    {
        IServiceCollection services = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.UseCosmosReminderService((OptionsBuilder<CosmosReminderTableOptions> _) => { }));

        Assert.Equal("services", exception.ParamName);
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
