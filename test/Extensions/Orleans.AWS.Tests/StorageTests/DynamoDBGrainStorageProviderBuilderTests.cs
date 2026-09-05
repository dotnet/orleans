using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;

namespace AWSUtils.Tests.StorageTests;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Persistence")]
[TestCategory("BVT")]
[TestCategory("AWS")]
[TestCategory("DynamoDB")]
[TestCategory("Persistence")]
public sealed class DynamoDBGrainStorageProviderBuilderTests
{
    private const string ProviderName = "configured-storage";

    [Fact]
    public void Configure_MinimalConfiguration_RegistersCredentialFreeDefaults()
    {
        var (builder, defaultSerializer) = ConfigureBuilder();

        using var services = builder.Services.BuildServiceProvider();
        var options = GetOptions(services);

        Assert.Null(options.AccessKey);
        Assert.Null(options.SecretKey);
        Assert.Null(options.Token);
        Assert.Null(options.ProfileName);
        Assert.Null(options.Service);
        Assert.Equal(string.Empty, options.ServiceId);
        Assert.Equal("OrleansGrainState", options.TableName);
        Assert.Equal(10, options.ReadCapacityUnits);
        Assert.Equal(5, options.WriteCapacityUnits);
        Assert.True(options.UseProvisionedThroughput);
        Assert.True(options.CreateIfNotExists);
        Assert.True(options.UpdateIfExists);
        Assert.False(options.DeleteStateOnClear);
        Assert.Null(options.TimeToLive);
        Assert.Same(defaultSerializer, options.GrainStorageSerializer);
    }

    [Fact]
    public void Configure_FullConfiguration_BindsEverySupportedSetting()
    {
        const string serializerKey = "custom-serializer";
        var keyedSerializer = new FakeGrainStorageSerializer();
        var (builder, defaultSerializer) = ConfigureBuilder(
            (nameof(DynamoDBStorageOptions.Service), "service-sentinel"),
            ("Region", "region-sentinel"),
            (nameof(DynamoDBStorageOptions.AccessKey), "access-sentinel"),
            (nameof(DynamoDBStorageOptions.SecretKey), "secret-sentinel"),
            (nameof(DynamoDBStorageOptions.Token), "token-sentinel"),
            (nameof(DynamoDBStorageOptions.ProfileName), "profile-sentinel"),
            (nameof(DynamoDBStorageOptions.ServiceId), "service-id-sentinel"),
            (nameof(DynamoDBStorageOptions.TableName), "table-sentinel"),
            (nameof(DynamoDBStorageOptions.ReadCapacityUnits), "23"),
            (nameof(DynamoDBStorageOptions.WriteCapacityUnits), "17"),
            (nameof(DynamoDBStorageOptions.UseProvisionedThroughput), "false"),
            (nameof(DynamoDBStorageOptions.CreateIfNotExists), "false"),
            (nameof(DynamoDBStorageOptions.UpdateIfExists), "false"),
            (nameof(DynamoDBStorageOptions.DeleteStateOnClear), "true"),
            (nameof(DynamoDBStorageOptions.TimeToLive), "01:02:03"),
            ("SerializerKey", serializerKey));
        builder.Services.AddKeyedSingleton<IGrainStorageSerializer>(serializerKey, keyedSerializer);

        using var services = builder.Services.BuildServiceProvider();
        var options = GetOptions(services);

        Assert.Equal("service-sentinel", options.Service);
        Assert.NotEqual("region-sentinel", options.Service);
        Assert.Equal("access-sentinel", options.AccessKey);
        Assert.Equal("secret-sentinel", options.SecretKey);
        Assert.Equal("token-sentinel", options.Token);
        Assert.Equal("profile-sentinel", options.ProfileName);
        Assert.Equal("service-id-sentinel", options.ServiceId);
        Assert.Equal("table-sentinel", options.TableName);
        Assert.Equal(23, options.ReadCapacityUnits);
        Assert.Equal(17, options.WriteCapacityUnits);
        Assert.False(options.UseProvisionedThroughput);
        Assert.False(options.CreateIfNotExists);
        Assert.False(options.UpdateIfExists);
        Assert.True(options.DeleteStateOnClear);
        Assert.Equal(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3), options.TimeToLive);
        Assert.Same(keyedSerializer, options.GrainStorageSerializer);
        Assert.NotSame(defaultSerializer, options.GrainStorageSerializer);
    }

    [Fact]
    public void Configure_RegionWithoutService_UsesRegion()
    {
        var (builder, _) = ConfigureBuilder(("Region", "region-sentinel"));

        using var services = builder.Services.BuildServiceProvider();
        var options = GetOptions(services);

        Assert.Equal("region-sentinel", options.Service);
        Assert.Null(options.AccessKey);
    }

    [Theory]
    [InlineData(nameof(DynamoDBStorageOptions.ReadCapacityUnits), "not-an-integer")]
    [InlineData(nameof(DynamoDBStorageOptions.WriteCapacityUnits), "not-an-integer")]
    [InlineData(nameof(DynamoDBStorageOptions.UseProvisionedThroughput), "not-a-boolean")]
    [InlineData(nameof(DynamoDBStorageOptions.CreateIfNotExists), "not-a-boolean")]
    [InlineData(nameof(DynamoDBStorageOptions.UpdateIfExists), "not-a-boolean")]
    [InlineData(nameof(DynamoDBStorageOptions.DeleteStateOnClear), "not-a-boolean")]
    [InlineData(nameof(DynamoDBStorageOptions.TimeToLive), "not-a-timespan")]
    public void Configure_InvalidTypedValue_ThrowsConfigurationException(string key, string invalidValue)
    {
        var (builder, _) = ConfigureBuilder((key, invalidValue));
        using var services = builder.Services.BuildServiceProvider();

        var exception = Assert.Throws<OrleansConfigurationException>(() => GetOptions(services));

        Assert.Contains("Storage", exception.Message, StringComparison.Ordinal);
        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.Contains(invalidValue, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Configure_DistinctTokenAndProfileName_MapFromTheirOwnKeys()
    {
        var (builder, _) = ConfigureBuilder(
            (nameof(DynamoDBStorageOptions.SecretKey), "secret-sentinel"),
            (nameof(DynamoDBStorageOptions.Token), "token-sentinel"),
            (nameof(DynamoDBStorageOptions.ProfileName), "profile-sentinel"));

        using var services = builder.Services.BuildServiceProvider();
        var options = GetOptions(services);

        Assert.Equal("token-sentinel", options.Token);
        Assert.NotEqual("secret-sentinel", options.Token);
        Assert.Equal("profile-sentinel", options.ProfileName);
        Assert.NotEqual("secret-sentinel", options.ProfileName);
    }

    [Fact]
    public void Configure_SerializerKey_ResolvesMatchingKeyedSerializer()
    {
        const string serializerKey = "custom-serializer";
        var keyedSerializer = new FakeGrainStorageSerializer();
        var (builder, defaultSerializer) = ConfigureBuilder(("SerializerKey", serializerKey));
        builder.Services.AddKeyedSingleton<IGrainStorageSerializer>(serializerKey, keyedSerializer);

        using var services = builder.Services.BuildServiceProvider();
        var options = GetOptions(services);

        Assert.Same(keyedSerializer, options.GrainStorageSerializer);
        Assert.NotSame(defaultSerializer, options.GrainStorageSerializer);
    }

    [Fact]
    public void Configure_WithoutSerializerKey_UsesDefaultSerializer()
    {
        var keyedSerializer = new FakeGrainStorageSerializer();
        var (builder, defaultSerializer) = ConfigureBuilder();
        builder.Services.AddKeyedSingleton<IGrainStorageSerializer>("unused-serializer", keyedSerializer);

        using var services = builder.Services.BuildServiceProvider();
        var options = GetOptions(services);

        Assert.Same(defaultSerializer, options.GrainStorageSerializer);
        Assert.NotSame(keyedSerializer, options.GrainStorageSerializer);
    }

    [Fact]
    public void Configure_RegistersNamedOptionsValidatorAndKeyedStorageDescriptor()
    {
        var (builder, _) = ConfigureBuilder(
            (nameof(DynamoDBStorageOptions.Service), "us-east-1"),
            (nameof(DynamoDBStorageOptions.TableName), "named-table"),
            (nameof(DynamoDBStorageOptions.ServiceId), "named-service"));
        var storageDescriptor = Assert.Single(
            builder.Services,
            descriptor => descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(IGrainStorage)
                && Equals(descriptor.ServiceKey, ProviderName));

        Assert.Equal(ProviderName, storageDescriptor.ServiceKey);
        Assert.Equal(typeof(IGrainStorage), storageDescriptor.ServiceType);
        Assert.NotNull(storageDescriptor.KeyedImplementationFactory);

        using var services = builder.Services.BuildServiceProvider();
        var monitor = services.GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>();
        var namedOptions = monitor.Get(ProviderName);
        var defaultOptions = monitor.Get(Options.DefaultName);
        var validator = Assert.IsType<DynamoDBGrainStorageOptionsValidator>(
            Assert.Single(services.GetServices<IConfigurationValidator>()));

        Assert.Equal("named-table", namedOptions.TableName);
        Assert.Equal("named-service", namedOptions.ServiceId);
        Assert.Equal("OrleansGrainState", defaultOptions.TableName);
        Assert.Equal(string.Empty, defaultOptions.ServiceId);
        validator.ValidateConfiguration();
    }

    [Fact]
    public void ValidateConfiguration_ValidProvisionedOptions_Succeeds()
    {
        var options = CreateValidOptions();
        var validator = new DynamoDBGrainStorageOptionsValidator(options, ProviderName);

        var exception = Record.Exception(validator.ValidateConfiguration);

        Assert.Null(exception);
        Assert.True(options.UseProvisionedThroughput);
        Assert.Equal(11, options.ReadCapacityUnits);
        Assert.Equal(7, options.WriteCapacityUnits);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateConfiguration_BlankTableName_Throws(string? tableName)
    {
        var options = CreateValidOptions();
        options.TableName = tableName!;
        var validator = new DynamoDBGrainStorageOptionsValidator(options, ProviderName);

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains(ProviderName, exception.Message);
        Assert.Contains(nameof(DynamoDBStorageOptions.TableName), exception.Message);
    }

    [Fact]
    public void ValidateConfiguration_ProvisionedReadCapacityZero_Throws()
    {
        var options = CreateValidOptions();
        options.ReadCapacityUnits = 0;
        var validator = new DynamoDBGrainStorageOptionsValidator(options, ProviderName);

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains(ProviderName, exception.Message);
        Assert.Contains(nameof(DynamoDBStorageOptions.ReadCapacityUnits), exception.Message);
        Assert.DoesNotContain(nameof(DynamoDBStorageOptions.WriteCapacityUnits), exception.Message);
    }

    [Fact]
    public void ValidateConfiguration_ProvisionedWriteCapacityZero_Throws()
    {
        var options = CreateValidOptions();
        options.WriteCapacityUnits = 0;
        var validator = new DynamoDBGrainStorageOptionsValidator(options, ProviderName);

        var exception = Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);

        Assert.Contains(ProviderName, exception.Message);
        Assert.Contains(nameof(DynamoDBStorageOptions.WriteCapacityUnits), exception.Message);
        Assert.DoesNotContain(nameof(DynamoDBStorageOptions.ReadCapacityUnits), exception.Message);
    }

    [Fact]
    public void ValidateConfiguration_NonProvisionedZeroCapacities_Succeeds()
    {
        var options = CreateValidOptions();
        options.UseProvisionedThroughput = false;
        options.ReadCapacityUnits = 0;
        options.WriteCapacityUnits = 0;
        var validator = new DynamoDBGrainStorageOptionsValidator(options, ProviderName);

        var exception = Record.Exception(validator.ValidateConfiguration);

        Assert.Null(exception);
        Assert.False(options.UseProvisionedThroughput);
        Assert.Equal(0, options.ReadCapacityUnits);
        Assert.Equal(0, options.WriteCapacityUnits);
    }

    private static (TestSiloBuilder Builder, IGrainStorageSerializer DefaultSerializer) ConfigureBuilder(
        params (string Key, string? Value)[] settings)
    {
        var values = settings.ToDictionary(
            setting => $"Storage:{setting.Key}",
            setting => setting.Value);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var builder = new TestSiloBuilder(configuration);
        var defaultSerializer = new FakeGrainStorageSerializer();
        builder.Services.AddSingleton<IGrainStorageSerializer>(defaultSerializer);

        new DynamoDBGrainStorageProviderBuilder().Configure(
            builder,
            ProviderName,
            configuration.GetSection("Storage"));

        return (builder, defaultSerializer);
    }

    private static DynamoDBStorageOptions GetOptions(IServiceProvider services)
        => services.GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>().Get(ProviderName);

    private static DynamoDBStorageOptions CreateValidOptions() => new()
    {
        Service = "us-east-1",
        TableName = "valid-table",
        UseProvisionedThroughput = true,
        ReadCapacityUnits = 11,
        WriteCapacityUnits = 7,
    };

    private sealed class TestSiloBuilder(IConfiguration configuration) : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = configuration;
    }

    private sealed class FakeGrainStorageSerializer : IGrainStorageSerializer
    {
        public BinaryData Serialize<T>(T? input) => throw new NotSupportedException();

        public T? Deserialize<T>(BinaryData input) => throw new NotSupportedException();
    }
}
