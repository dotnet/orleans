using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Clustering.Cosmos;
using Orleans.Hosting;

namespace Tester.Cosmos.Clustering;

[TestCategory("Cosmos"), TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("Cosmos")]
[TestArea("Clustering")]
public class CosmosClusteringHostingExtensionsTests
{
    [Fact]
    public void UseCosmosClustering_NullBuilderWithOptionsCallback_Throws()
    {
        ISiloBuilder builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.UseCosmosClustering((CosmosClusteringOptions _) => { }));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void UseCosmosClustering_NullOptionsCallbackWithBuilder_ThrowsBeforeRegisteringServices()
    {
        var builder = new TestSiloBuilder();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.UseCosmosClustering((Action<CosmosClusteringOptions>)null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Empty(builder.Services);
    }

    [Fact]
    public void UseCosmosClustering_NullBuilderWithOptionsBuilderCallback_Throws()
    {
        ISiloBuilder builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.UseCosmosClustering((OptionsBuilder<CosmosClusteringOptions> _) => { }));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void UseCosmosClustering_NullOptionsBuilderCallbackWithBuilder_ThrowsBeforeRegisteringServices()
    {
        var builder = new TestSiloBuilder();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.UseCosmosClustering((Action<OptionsBuilder<CosmosClusteringOptions>>)null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Empty(builder.Services);
    }

    [Fact]
    public void UseCosmosClustering_NullBuilderWithoutCallback_Throws()
    {
        ISiloBuilder builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() => builder.UseCosmosClustering());

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void UseCosmosGatewayListProvider_NullBuilderWithOptionsCallback_Throws()
    {
        IClientBuilder builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.UseCosmosGatewayListProvider((CosmosClusteringOptions _) => { }));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void UseCosmosGatewayListProvider_NullOptionsCallbackWithBuilder_ThrowsBeforeRegisteringServices()
    {
        var builder = new TestClientBuilder();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.UseCosmosGatewayListProvider((Action<CosmosClusteringOptions>)null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Empty(builder.Services);
    }

    [Fact]
    public void UseCosmosGatewayListProvider_NullBuilderWithOptionsBuilderCallback_Throws()
    {
        IClientBuilder builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.UseCosmosGatewayListProvider((OptionsBuilder<CosmosClusteringOptions> _) => { }));

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void UseCosmosGatewayListProvider_NullOptionsBuilderCallbackWithBuilder_ThrowsBeforeRegisteringServices()
    {
        var builder = new TestClientBuilder();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            builder.UseCosmosGatewayListProvider((Action<OptionsBuilder<CosmosClusteringOptions>>)null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Empty(builder.Services);
    }

    [Fact]
    public void UseCosmosGatewayListProvider_NullBuilderWithoutCallback_Throws()
    {
        IClientBuilder builder = null!;

        var exception = Assert.Throws<ArgumentNullException>(() => builder.UseCosmosGatewayListProvider());

        Assert.Equal("builder", exception.ParamName);
    }

    [Fact]
    public void UseCosmosClustering_NullServicesWithOptionsCallback_Throws()
    {
        IServiceCollection services = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.UseCosmosClustering((CosmosClusteringOptions _) => { }));

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void UseCosmosClustering_NullOptionsCallback_ThrowsBeforeRegisteringServices()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.UseCosmosClustering((Action<CosmosClusteringOptions>)null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Empty(services);
    }

    [Fact]
    public void UseCosmosClustering_NullServicesWithOptionsBuilderCallback_Throws()
    {
        IServiceCollection services = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.UseCosmosClustering((OptionsBuilder<CosmosClusteringOptions> _) => { }));

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void UseCosmosClustering_NullOptionsBuilderCallback_ThrowsBeforeRegisteringServices()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.UseCosmosClustering((Action<OptionsBuilder<CosmosClusteringOptions>>)null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Empty(services);
    }

    [Fact]
    public void UseCosmosGatewayListProvider_NullServicesWithOptionsCallback_Throws()
    {
        IServiceCollection services = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.UseCosmosGatewayListProvider((CosmosClusteringOptions _) => { }));

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void UseCosmosGatewayListProvider_NullOptionsCallback_ThrowsBeforeRegisteringServices()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.UseCosmosGatewayListProvider((Action<CosmosClusteringOptions>)null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Empty(services);
    }

    [Fact]
    public void UseCosmosGatewayListProvider_NullServicesWithOptionsBuilderCallback_Throws()
    {
        IServiceCollection services = null!;

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.UseCosmosGatewayListProvider((OptionsBuilder<CosmosClusteringOptions> _) => { }));

        Assert.Equal("services", exception.ParamName);
    }

    [Fact]
    public void UseCosmosGatewayListProvider_NullOptionsBuilderCallback_ThrowsBeforeRegisteringServices()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<ArgumentNullException>(() =>
            services.UseCosmosGatewayListProvider((Action<OptionsBuilder<CosmosClusteringOptions>>)null!));

        Assert.Equal("configureOptions", exception.ParamName);
        Assert.Empty(services);
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestClientBuilder : IClientBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
