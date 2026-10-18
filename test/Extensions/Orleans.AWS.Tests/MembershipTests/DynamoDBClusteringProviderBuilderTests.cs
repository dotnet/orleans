using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Providers;
using Xunit;

namespace AWSUtils.Tests.MembershipTests;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Clustering")]
[TestCategory("BVT")]
[TestCategory("AWS")]
[TestCategory("DynamoDB")]
[TestCategory("Clustering")]
public sealed class DynamoDBClusteringProviderBuilderTests
{
    [Theory]
    [InlineData("token-sentinel", "profile-sentinel")]
    [InlineData(null, null)]
    public void Configure_Silo_CredentialKeysBindIndependently(string? token, string? profileName)
    {
        var configuration = CreateConfiguration(token, profileName);
        var builder = new TestSiloBuilder(configuration);
        IProviderBuilder<ISiloBuilder> provider = new DynamoDBClusteringProviderBuilder();

        provider.Configure(builder, null, configuration.GetSection("Clustering"));

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<DynamoDBClusteringOptions>>().Value;
        Assert.Equal("secret-sentinel", options.SecretKey);
        Assert.Equal(token, options.Token);
        Assert.Equal(profileName, options.ProfileName);
    }

    [Theory]
    [InlineData("token-sentinel", "profile-sentinel")]
    [InlineData(null, null)]
    public void Configure_Client_CredentialKeysBindIndependently(string? token, string? profileName)
    {
        var configuration = CreateConfiguration(token, profileName);
        var builder = new TestClientBuilder(configuration);
        IProviderBuilder<IClientBuilder> provider = new DynamoDBClusteringProviderBuilder();

        provider.Configure(builder, null, configuration.GetSection("Clustering"));

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<DynamoDBGatewayOptions>>().Value;
        Assert.Equal("secret-sentinel", options.SecretKey);
        Assert.Equal(token, options.Token);
        Assert.Equal(profileName, options.ProfileName);
    }

    [Theory]
    [InlineData(nameof(DynamoDBClusteringOptions.ReadCapacityUnits), "not-an-integer", typeof(int))]
    [InlineData(nameof(DynamoDBClusteringOptions.WriteCapacityUnits), "not-an-integer", typeof(int))]
    [InlineData(nameof(DynamoDBClusteringOptions.UseProvisionedThroughput), "not-a-boolean", typeof(bool))]
    [InlineData(nameof(DynamoDBClusteringOptions.CreateIfNotExists), "not-a-boolean", typeof(bool))]
    [InlineData(nameof(DynamoDBClusteringOptions.UpdateIfExists), "not-a-boolean", typeof(bool))]
    public void Configure_Silo_InvalidTypedValue_Throws(string key, string invalidValue, Type expectedType)
    {
        var configuration = CreateSingleValueConfiguration(key, invalidValue);
        var builder = new TestSiloBuilder(configuration);
        IProviderBuilder<ISiloBuilder> provider = new DynamoDBClusteringProviderBuilder();

        provider.Configure(builder, null, configuration.GetSection("Clustering"));

        using var services = builder.Services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(
            () => services.GetRequiredService<IOptions<DynamoDBClusteringOptions>>().Value);
        Assert.Contains($"at 'Clustering:{key}'", exception.Message);
        Assert.Contains($"type '{expectedType}'", exception.Message);
    }

    [Theory]
    [InlineData(nameof(DynamoDBGatewayOptions.ReadCapacityUnits), "not-an-integer", typeof(int))]
    [InlineData(nameof(DynamoDBGatewayOptions.WriteCapacityUnits), "not-an-integer", typeof(int))]
    [InlineData(nameof(DynamoDBGatewayOptions.UseProvisionedThroughput), "not-a-boolean", typeof(bool))]
    [InlineData(nameof(DynamoDBGatewayOptions.CreateIfNotExists), "not-a-boolean", typeof(bool))]
    [InlineData(nameof(DynamoDBGatewayOptions.UpdateIfExists), "not-a-boolean", typeof(bool))]
    public void Configure_Client_InvalidTypedValue_Throws(string key, string invalidValue, Type expectedType)
    {
        var configuration = CreateSingleValueConfiguration(key, invalidValue);
        var builder = new TestClientBuilder(configuration);
        IProviderBuilder<IClientBuilder> provider = new DynamoDBClusteringProviderBuilder();

        provider.Configure(builder, null, configuration.GetSection("Clustering"));

        using var services = builder.Services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(
            () => services.GetRequiredService<IOptions<DynamoDBGatewayOptions>>().Value);
        Assert.Contains($"at 'Clustering:{key}'", exception.Message);
        Assert.Contains($"type '{expectedType}'", exception.Message);
    }

    private static IConfiguration CreateSingleValueConfiguration(string key, string value)
        => new ConfigurationBuilder()
            .AddInMemoryCollection([new($"Clustering:{key}", value)])
            .Build();

    private static IConfiguration CreateConfiguration(string? token, string? profileName)
    {
        var values = new Dictionary<string, string?>
        {
            [$"Clustering:{nameof(DynamoDBClusteringOptions.SecretKey)}"] = "secret-sentinel",
            [$"Clustering:{nameof(DynamoDBClusteringOptions.Token)}"] = token,
            [$"Clustering:{nameof(DynamoDBClusteringOptions.ProfileName)}"] = profileName,
        };
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values.Where(setting => setting.Value is not null))
            .Build();
    }

    private sealed class TestSiloBuilder(IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = configuration;
    }

    private sealed class TestClientBuilder(IConfiguration configuration) : IClientBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = configuration;
    }
}
