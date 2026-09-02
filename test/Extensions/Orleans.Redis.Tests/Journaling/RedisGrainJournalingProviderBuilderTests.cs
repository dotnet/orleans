using System.Net;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Runtime;
using StackExchange.Redis;
using TestExtensions;
using Xunit;

namespace Tester.Redis.Journaling;

[TestSuite("BVT")]
[TestProvider("Redis")]
[TestArea("Journaling")]
[TestCategory("BVT")]
[TestCategory("Redis")]
public sealed class RedisGrainJournalingProviderBuilderTests
{
    private const string ConfigurationSectionName = "Orleans:GrainJournaling:Redis";

    [Fact]
    public void Configure_FullConfiguration_BindsSettingsAndJournalFormat()
    {
        var builder = ConfigureBuilder(
            ("ConnectionString", "localhost:6380,password=secret,ssl=true,abortConnect=false"),
            (nameof(RedisJournalStorageOptions.KeyPrefix), "configured-prefix"),
            (nameof(RedisJournalStorageOptions.CompactionThresholdBytes), "123456"),
            (nameof(RedisJournalStorageOptions.ReadChunkSize), "789"),
            (nameof(RedisJournalStorageOptions.InitStage), "1234"),
            (nameof(JournaledStateManagerOptions.JournalFormatKey), "configured-format"));

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<RedisJournalStorageOptions>>().Value;
        var managerOptions = services.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value;

        Assert.NotNull(options.ConfigurationOptions);
        var endpoint = Assert.IsType<DnsEndPoint>(Assert.Single(options.ConfigurationOptions.EndPoints));
        Assert.Equal("localhost", endpoint.Host);
        Assert.Equal(6380, endpoint.Port);
        Assert.Equal("secret", options.ConfigurationOptions.Password);
        Assert.True(options.ConfigurationOptions.Ssl);
        Assert.False(options.ConfigurationOptions.AbortOnConnectFail);
        Assert.Equal("configured-prefix", options.KeyPrefix);
        Assert.Equal(123456, options.CompactionThresholdBytes);
        Assert.Equal(789, options.ReadChunkSize);
        Assert.Equal(1234, options.InitStage);
        Assert.Equal("configured-format", managerOptions.JournalFormatKey);
    }

    [Fact]
    public void Configure_ConnectionNameWithoutConnectionString_UsesRootConnectionString()
    {
        const string connectionString = "named-host:6381,abortConnect=false";
        var builder = ConfigureBuilder(
            [("ConnectionName", "journal-redis")],
            [("ConnectionStrings:journal-redis", connectionString)]);

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<RedisJournalStorageOptions>>().Value;

        Assert.NotNull(options.ConfigurationOptions);
        var endpoint = Assert.IsType<DnsEndPoint>(Assert.Single(options.ConfigurationOptions.EndPoints));
        Assert.Equal("named-host", endpoint.Host);
        Assert.Equal(6381, endpoint.Port);
        Assert.False(options.ConfigurationOptions.AbortOnConnectFail);
    }

    [Fact]
    public void Configure_ExplicitConnectionString_TakesPrecedenceOverNamedConnection()
    {
        var builder = ConfigureBuilder(
            [
                ("ConnectionName", "journal-redis"),
                ("ConnectionString", "explicit-host:6382,abortConnect=false"),
            ],
            [("ConnectionStrings:journal-redis", "named-host:6381,abortConnect=false")]);

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<RedisJournalStorageOptions>>().Value;

        Assert.NotNull(options.ConfigurationOptions);
        var endpoint = Assert.IsType<DnsEndPoint>(Assert.Single(options.ConfigurationOptions.EndPoints));
        Assert.Equal("explicit-host", endpoint.Host);
        Assert.Equal(6382, endpoint.Port);
    }

    [Fact]
    public async Task Configure_ServiceKey_UsesSharedKeyedMultiplexerAndIgnoresConnectionSettings()
    {
        const string serviceKey = "shared-multiplexer";
        var multiplexer = DispatchProxy.Create<IConnectionMultiplexer, ConnectionMultiplexerProxy>();
        var builder = ConfigureBuilder(
            ("ServiceKey", serviceKey),
            ("ConnectionString", "not a valid Redis connection string"));
        builder.Services.AddKeyedSingleton(serviceKey, multiplexer);

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<RedisJournalStorageOptions>>().Value;
        var (configuredMultiplexer, isShared) = await options.CreateMultiplexer(options);

        Assert.Same(multiplexer, configuredMultiplexer);
        Assert.True(isShared);
        Assert.Null(options.ConfigurationOptions);
    }

    [Fact]
    public void Configure_BlankAndInvalidValues_PreserveDefaults()
    {
        var expected = new RedisJournalStorageOptions();
        var expectedManagerOptions = new JournaledStateManagerOptions();
        var builder = ConfigureBuilder(
            ("ServiceKey", ""),
            ("ConnectionName", ""),
            ("ConnectionString", ""),
            (nameof(RedisJournalStorageOptions.KeyPrefix), " "),
            (nameof(RedisJournalStorageOptions.CompactionThresholdBytes), "not-a-long"),
            (nameof(RedisJournalStorageOptions.ReadChunkSize), "not-an-int"),
            (nameof(RedisJournalStorageOptions.InitStage), "not-an-int"),
            (nameof(JournaledStateManagerOptions.JournalFormatKey), " "));

        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<RedisJournalStorageOptions>>().Value;
        var managerOptions = services.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value;

        Assert.Null(options.ConfigurationOptions);
        Assert.Equal(expected.CreateMultiplexer, options.CreateMultiplexer);
        Assert.Null(options.KeyPrefix);
        Assert.Equal(expected.CompactionThresholdBytes, options.CompactionThresholdBytes);
        Assert.Equal(expected.ReadChunkSize, options.ReadChunkSize);
        Assert.Equal(expected.InitStage, options.InitStage);
        Assert.Equal(expectedManagerOptions.JournalFormatKey, managerOptions.JournalFormatKey);
    }

    [Fact]
    public void Configure_RegistersJournalStorageServicesExactlyOnce()
    {
        var builder = ConfigureBuilder();

        Assert.Single(builder.Services, service => service.ServiceType == typeof(RedisJournalStorageProvider));
        Assert.Single(builder.Services, service => service.ServiceType == typeof(IJournalStorageProvider));
        Assert.Single(builder.Services, service => service.ServiceType == typeof(IJournalStorageCatalog));
        Assert.Single(
            builder.Services,
            service => service.ServiceType == typeof(ILifecycleParticipant<ISiloLifecycle>)
                && service.ImplementationFactory is not null);
        Assert.Single(builder.Services, service => service.ServiceType == typeof(IConfigurationValidator));
    }

    private static TestSiloBuilder ConfigureBuilder(params (string Key, string? Value)[] settings)
        => ConfigureBuilder(settings, []);

    private static TestSiloBuilder ConfigureBuilder(
        (string Key, string? Value)[] settings,
        (string Key, string? Value)[] rootSettings)
    {
        var values = rootSettings
            .Concat(settings.Select(setting => ($"{ConfigurationSectionName}:{setting.Key}", setting.Value)))
            .ToDictionary(setting => setting.Item1, setting => setting.Item2);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var builder = new TestSiloBuilder(configuration);
        builder.Services.AddSingleton<IConfiguration>(configuration);

        new RedisGrainJournalingProviderBuilder().Configure(
            builder,
            "redis",
            configuration.GetSection(ConfigurationSectionName));

        return builder;
    }

    private sealed class TestSiloBuilder(IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = configuration;
    }

    public class ConnectionMultiplexerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
    }
}
