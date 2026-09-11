using System.Reflection;
using Amazon;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans;
using Orleans.AWSUtils.Tests;
using Orleans.Clustering.DynamoDB;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Storage;
using Xunit;
using ClusteringProviderConfiguration = Orleans.Clustering.DynamoDB.DynamoDBProviderConfiguration;
using LinkedDynamoDBStorage = Orleans.AWSUtils.Tests.DynamoDBStorage;

namespace AWSUtils.Tests.Configuration;

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Storage")]
[TestCategory("AWS"), TestCategory("DynamoDB"), TestCategory("BVT")]
public sealed class DynamoDBProviderConfigurationTests
{
    [Fact]
    public void Create_Token_BindsSessionToken()
    {
        var (_, provider, options) = Bind(new() { ["Provider:Token"] = "direct-token" });

        Assert.Equal("direct-token", options.Token);
        Assert.Equal("direct-token", provider.GetValue("Token", "SessionToken"));
    }

    [Fact]
    public void Create_SessionTokenAlias_BindsSessionToken()
    {
        var (_, provider, options) = Bind(new() { ["Provider:SessionToken"] = "alias-token" });

        Assert.Equal("alias-token", options.Token);
        Assert.Equal("alias-token", provider.GetValue("Token", "SessionToken"));
    }

    [Fact]
    public void Create_ProfileName_BindsProfileName()
    {
        var (_, provider, options) = Bind(new() { ["Provider:ProfileName"] = "direct-profile" });

        Assert.Equal("direct-profile", options.ProfileName);
        Assert.Equal("direct-profile", provider.GetValue("ProfileName", "Profile"));
    }

    [Fact]
    public void Create_ProfileAlias_BindsProfileName()
    {
        var (_, provider, options) = Bind(new() { ["Provider:Profile"] = "alias-profile" });

        Assert.Equal("alias-profile", options.ProfileName);
        Assert.Equal("alias-profile", provider.GetValue("ProfileName", "Profile"));
    }

    [Fact]
    public void Create_DirectValuesOverrideStructuredAndConnectionStringValues()
    {
        var (_, provider, options) = Bind(new()
        {
            ["Provider:ServiceKey"] = "orders",
            ["Provider:ConnectionProperties:AccessKey"] = "connection-properties-access",
            ["Provider:Resource:SecretKey"] = "resource-secret",
            ["Provider:AWS:Token"] = "aws-token",
            ["Provider:ConnectionProperties:ProfileName"] = "connection-properties-profile",
            ["Provider:AccessKey"] = "direct-access",
            ["Provider:SecretKey"] = "direct-secret",
            ["Provider:Token"] = "direct-token",
            ["Provider:ProfileName"] = "direct-profile",
            ["Provider:Service"] = "direct-region",
            ["Provider:TableName"] = "direct-table",
            ["AWS:Resources:orders:AccessKey"] = "resource-reference-access",
            ["AWS:Resources:orders:SecretKey"] = "resource-reference-secret",
            ["AWS:Resources:orders:Service"] = "resource-reference-region",
            ["ConnectionStrings:orders"] =
                "AccessKey=connection-access;SecretKey=connection-secret;Token=connection-token;" +
                "ProfileName=connection-profile;Service=connection-region;TableName=connection-table",
        });

        Assert.Equal("direct-access", options.AccessKey);
        Assert.Equal("direct-secret", options.SecretKey);
        Assert.Equal("direct-token", options.Token);
        Assert.Equal("direct-profile", options.ProfileName);
        Assert.Equal("direct-region", options.Service);
        Assert.Equal("direct-table", provider.GetValue("TableName"));
    }

    [Fact]
    public void Create_ConnectionPropertiesShape_BindsValues()
        => AssertProviderLocalShape("ConnectionProperties");

    [Fact]
    public void Create_ResourceShape_BindsValues()
        => AssertProviderLocalShape("Resource");

    [Fact]
    public void Create_ProviderLocalAWSShape_BindsValues()
        => AssertProviderLocalShape("AWS");

    [Fact]
    public void Create_AWSResourcesReference_BindsConnectionProperties()
    {
        var (_, provider, options) = Bind(new()
        {
            ["Provider:ServiceKey"] = "orders-table",
            ["AWS:Resources:orders-table:AccessKey"] = "structured-access",
            ["AWS:Resources:orders-table:SecretKey"] = "structured-secret",
            ["AWS:Resources:orders-table:Token"] = "structured-token",
            ["AWS:Resources:orders-table:ProfileName"] = "structured-profile",
            ["AWS:Resources:orders-table:Service"] = "structured-region",
            ["AWS:Resources:orders-table:TableName"] = "structured-table",
            ["AWS:Resources:orders-table:ServiceId"] = "structured-service-id",
            ["ORDERS_TABLE_ACCESSKEY"] = "encoded-access",
            ["ORDERS_TABLE_TABLENAME"] = "encoded-table",
            ["AWS_ENDPOINT_URL_DYNAMODB"] = "https://fallback.invalid",
        });

        Assert.Equal("structured-access", options.AccessKey);
        Assert.Equal("structured-secret", options.SecretKey);
        Assert.Equal("structured-token", options.Token);
        Assert.Equal("structured-profile", options.ProfileName);
        Assert.Equal("structured-region", options.Service);
        Assert.Equal("structured-table", provider.GetValue("TableName"));
        Assert.Equal("structured-service-id", provider.GetValue("ServiceId"));
    }

    [Theory]
    [InlineData("provider-region", "https://endpoint.invalid", "aws-section", "aws-region", "aws-default", "provider-region")]
    [InlineData(null, "https://endpoint.invalid", "aws-section", "aws-region", "aws-default", "https://endpoint.invalid")]
    [InlineData(null, null, "aws-section", "aws-region", "aws-default", "aws-section")]
    [InlineData(null, null, null, "aws-region", "aws-default", "aws-region")]
    [InlineData(null, null, null, null, "aws-default", "aws-default")]
    public void Create_ServiceFallback_UsesExpectedPrecedence(
        string? providerService,
        string? endpoint,
        string? awsSectionRegion,
        string? awsRegion,
        string? awsDefaultRegion,
        string expected)
    {
        var values = new Dictionary<string, string?>
        {
            ["Provider:Service"] = providerService,
            ["AWS_ENDPOINT_URL_DYNAMODB"] = endpoint,
            ["AWS:Region"] = awsSectionRegion,
            ["AWS_REGION"] = awsRegion,
            ["AWS_DEFAULT_REGION"] = awsDefaultRegion,
        };

        var (_, _, options) = Bind(values);

        Assert.Equal(expected, options.Service);
    }

    [Fact]
    public void Create_ConnectionStringAliases_BindAllValues()
    {
        var (_, provider, options) = Bind(new()
        {
            ["Provider:ConnectionString"] =
                "AccessKey=connection-access;SecretKey=connection-secret;SessionToken=connection-token;" +
                "Profile=connection-profile;Endpoint=https://localhost:8123;TableName=connection-table",
        });

        Assert.Equal("connection-access", options.AccessKey);
        Assert.Equal("connection-secret", options.SecretKey);
        Assert.Equal("connection-token", options.Token);
        Assert.Equal("connection-profile", options.ProfileName);
        Assert.Equal("https://localhost:8123", options.Service);
        Assert.Equal("connection-table", provider.GetValue("TableName"));
    }

    [Fact]
    public void Create_SemicolonDelimitedConnectionString_ParsesEndpointAndCredentials()
    {
        var (_, provider, options) = Bind(new()
        {
            ["Provider:ConnectionString"] =
                "Service=https://localhost:8123;AccessKey=connection-access;" +
                "SecretKey=connection-secret;TableName=connection-table",
        });

        Assert.Equal("https://localhost:8123", options.Service);
        Assert.Equal("connection-access", options.AccessKey);
        Assert.Equal("connection-secret", options.SecretKey);
        Assert.Equal("connection-table", provider.GetValue("TableName"));
    }

    [Fact]
    public void Create_MalformedConnectionString_FailsAsMissingOrIncompatibleConfiguration()
    {
        var (_, _, options) = Bind(new()
        {
            ["Provider:ConnectionString"] = "not-a-pair;also-not-a-pair",
        });

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => ClusteringProviderConfiguration.ValidateClientOptions(options, "test provider"));

        Assert.Contains(nameof(options.Service), exception.Message);
        Assert.Contains("test provider", exception.Message);
    }

    [Fact]
    public void Create_IncompleteConnectionString_FailsAsMissingConfiguration()
    {
        var (_, _, options) = Bind(new()
        {
            ["Provider:ConnectionString"] = "Service=us-west-2;AccessKey=unpaired-access",
        });

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => ClusteringProviderConfiguration.ValidateClientOptions(options, "test provider"));

        Assert.Contains(nameof(options.AccessKey), exception.Message);
        Assert.Contains(nameof(options.SecretKey), exception.Message);
        Assert.DoesNotContain("unpaired-access", exception.Message);
    }

    [Fact]
    public void ClusteringBuilder_MapsTableName()
    {
        using var host = BuildSiloProviderHost(
            new()
            {
                ["Provider:Service"] = "us-west-2",
                ["Provider:TableName"] = "clustering-table",
            },
            (context, silo) => new DynamoDBClusteringProviderBuilder().Configure(
                silo,
                name: null,
                context.Configuration.GetSection("Provider")));

        var options = host.Services.GetRequiredService<IOptions<DynamoDBClusteringOptions>>().Value;

        Assert.Equal("us-west-2", options.Service);
        Assert.Equal("clustering-table", options.TableName);
    }

    [Fact]
    public void GatewayBuilder_MapsTableName()
    {
        using var host = BuildClientProviderHost(
            new()
            {
                ["Provider:Service"] = "us-east-2",
                ["Provider:TableName"] = "gateway-table",
            },
            (context, client) => new DynamoDBClusteringProviderBuilder().Configure(
                client,
                name: null,
                context.Configuration.GetSection("Provider")));

        var options = host.Services.GetRequiredService<IOptions<DynamoDBGatewayOptions>>().Value;

        Assert.Equal("us-east-2", options.Service);
        Assert.Equal("gateway-table", options.TableName);
    }

    [Fact]
    public void RemindersBuilder_MapsTableName()
    {
        using var host = BuildSiloProviderHost(
            new()
            {
                ["Provider:Service"] = "eu-west-1",
                ["Provider:TableName"] = "reminder-table",
            },
            (context, silo) => new DynamoDBRemindersProviderBuilder().Configure(
                silo,
                name: null,
                context.Configuration.GetSection("Provider")));

        var options = host.Services.GetRequiredService<IOptions<DynamoDBReminderStorageOptions>>().Value;

        Assert.Equal("eu-west-1", options.Service);
        Assert.Equal("reminder-table", options.TableName);
    }

    [Fact]
    public void GrainStorageBuilder_MapsDirectOnlySettingsAndSerializer()
    {
        const string providerName = "phase-one-storage";
        const string serializerKey = "phase-one-serializer";
        var serializer = new StubGrainStorageSerializer();
        using var host = BuildSiloProviderHost(
            new()
            {
                ["Provider:Service"] = "ap-southeast-2",
                ["Provider:ServiceId"] = "phase-one-service",
                ["Provider:TableName"] = "grain-state-table",
                ["Provider:ReadCapacityUnits"] = "17",
                ["Provider:WriteCapacityUnits"] = "9",
                ["Provider:UseProvisionedThroughput"] = "false",
                ["Provider:CreateIfNotExists"] = "false",
                ["Provider:UpdateIfExists"] = "false",
                ["Provider:DeleteStateOnClear"] = "true",
                ["Provider:TimeToLive"] = "01:02:03",
                ["Provider:SerializerKey"] = serializerKey,
            },
            (context, silo) =>
            {
                silo.ConfigureServices(services =>
                    services.AddKeyedSingleton<IGrainStorageSerializer>(serializerKey, serializer));
                new DynamoDBGrainStorageProviderBuilder().Configure(
                    silo,
                    providerName,
                    context.Configuration.GetSection("Provider"));
            });

        var options = host.Services
            .GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>()
            .Get(providerName);

        Assert.Equal("ap-southeast-2", options.Service);
        Assert.Equal("phase-one-service", options.ServiceId);
        Assert.Equal("grain-state-table", options.TableName);
        Assert.Equal(17, options.ReadCapacityUnits);
        Assert.Equal(9, options.WriteCapacityUnits);
        Assert.False(options.UseProvisionedThroughput);
        Assert.False(options.CreateIfNotExists);
        Assert.False(options.UpdateIfExists);
        Assert.True(options.DeleteStateOnClear);
        Assert.Equal(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3), options.TimeToLive);
        Assert.Same(serializer, options.GrainStorageSerializer);
    }

    [Fact]
    public void AspireGeneratedConfiguration_ActivatesClusteringStorageAndReminders()
    {
        const string serviceKey = "dynamodb";
        const string serviceUrl = "http://localhost:8000";
        using var host = new HostBuilder()
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Orleans:Clustering:ProviderType"] = "DynamoDB",
                    ["Orleans:Clustering:ServiceKey"] = serviceKey,
                    ["Orleans:GrainStorage:Default:ProviderType"] = "DynamoDB",
                    ["Orleans:GrainStorage:Default:ServiceKey"] = serviceKey,
                    ["Orleans:Reminders:ProviderType"] = "DynamoDB",
                    ["Orleans:Reminders:ServiceKey"] = serviceKey,
                    ["AWS_ENDPOINT_URL_DYNAMODB"] = serviceUrl,
                }))
            .UseOrleans(_ => { })
            .Build();

        var clustering = host.Services.GetRequiredService<IOptions<DynamoDBClusteringOptions>>().Value;
        var storage = host.Services.GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>().Get("Default");
        var reminders = host.Services.GetRequiredService<IOptions<DynamoDBReminderStorageOptions>>().Value;

        Assert.Equal(serviceUrl, clustering.Service);
        Assert.Equal("OrleansSilos", clustering.TableName);
        Assert.Equal(serviceUrl, storage.Service);
        Assert.Equal("OrleansGrainState", storage.TableName);
        Assert.Equal(serviceUrl, reminders.Service);
        Assert.Equal("OrleansReminders", reminders.TableName);
    }

    private static void AssertProviderLocalShape(string shape)
    {
        var (_, provider, options) = Bind(new()
        {
            [$"Provider:{shape}:AccessKey"] = $"{shape}-access",
            [$"Provider:{shape}:SecretKey"] = $"{shape}-secret",
            [$"Provider:{shape}:Token"] = $"{shape}-token",
            [$"Provider:{shape}:ProfileName"] = $"{shape}-profile",
            [$"Provider:{shape}:Service"] = $"{shape}-region",
            [$"Provider:{shape}:TableName"] = $"{shape}-table",
            [$"Provider:{shape}:ServiceId"] = $"{shape}-service-id",
        });

        Assert.Equal($"{shape}-access", options.AccessKey);
        Assert.Equal($"{shape}-secret", options.SecretKey);
        Assert.Equal($"{shape}-token", options.Token);
        Assert.Equal($"{shape}-profile", options.ProfileName);
        Assert.Equal($"{shape}-region", options.Service);
        Assert.Equal($"{shape}-table", provider.GetValue("TableName"));
        Assert.Equal($"{shape}-service-id", provider.GetValue("ServiceId"));
    }

    private static (
        IConfigurationRoot Configuration,
        ClusteringProviderConfiguration Provider,
        DynamoDBClusteringOptions Options) Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var provider = ClusteringProviderConfiguration.Create(
            configuration.GetSection("Provider"),
            configuration);
        var options = new DynamoDBClusteringOptions();
        provider.ConfigureClientOptions(options);
        return (configuration, provider, options);
    }

    private static IHost BuildSiloProviderHost(
        Dictionary<string, string?> values,
        Action<HostBuilderContext, ISiloBuilder> configure)
        => new HostBuilder()
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values))
            .UseOrleans(configure)
            .Build();

    private static IHost BuildClientProviderHost(
        Dictionary<string, string?> values,
        Action<HostBuilderContext, IClientBuilder> configure)
        => new HostBuilder()
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values))
            .UseOrleansClient(configure)
            .Build();

    private sealed class StubGrainStorageSerializer : IGrainStorageSerializer
    {
        public BinaryData Serialize<T>(T? input) => throw new NotSupportedException();

        public T? Deserialize<T>(BinaryData input) => throw new NotSupportedException();
    }
}

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Storage")]
[TestCategory("AWS"), TestCategory("DynamoDB"), TestCategory("BVT")]
public sealed class DynamoDBClientOptionsTests
{
    [Fact]
    public void CredentialProperties_HaveRedactAttribute()
    {
        foreach (var propertyName in new[]
        {
            nameof(DynamoDBStorageOptions.AccessKey),
            nameof(DynamoDBStorageOptions.SecretKey),
            nameof(DynamoDBStorageOptions.Token),
        })
        {
            var property = typeof(DynamoDBStorageOptions).GetProperty(propertyName);
            var redaction = property?.GetCustomAttribute<RedactAttribute>();

            Assert.NotNull(redaction);
            Assert.Equal("REDACTED", redaction.Redact($"literal-{propertyName}")?.ToString());
        }
    }

    [Fact]
    public void RegisteredOptionsFormatter_DoesNotRenderCredentialLiterals()
    {
        using var host = new HostBuilder()
            .UseOrleans(silo => silo.UseDynamoDBClustering((DynamoDBClusteringOptions options) =>
            {
                options.AccessKey = "literal-access";
                options.SecretKey = "literal-secret";
                options.Token = "literal-token";
                options.ProfileName = "visible-profile";
                options.Service = "visible-region";
                options.TableName = "visible-table";
            }))
            .Build();
        var formatter = host.Services
            .GetServices<IOptionFormatter>()
            .Single(value => value is IOptionFormatter<DynamoDBClusteringOptions>);

        var formatted = string.Join(Environment.NewLine, formatter.Format());

        Assert.DoesNotContain("literal-access", formatted);
        Assert.DoesNotContain("literal-secret", formatted);
        Assert.DoesNotContain("literal-token", formatted);
        Assert.Contains("AccessKey: REDACTED", formatted);
        Assert.Contains("SecretKey: REDACTED", formatted);
        Assert.Contains("Token: REDACTED", formatted);
        Assert.Contains("ProfileName: visible-profile", formatted);
        Assert.Contains("Service: visible-region", formatted);
        Assert.Contains("TableName: visible-table", formatted);
    }
}

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Storage")]
[TestCategory("AWS"), TestCategory("DynamoDB"), TestCategory("BVT")]
public sealed class DynamoDBOptionsValidationTests
{
    [Fact]
    public void RegisteredValidator_MissingService_ThrowsWithServiceSettingName()
    {
        var exception = AssertClusteringValidationFailure(_ => { });

        Assert.Contains(nameof(DynamoDBClusteringOptions.Service), exception.Message);
        Assert.Contains("DynamoDB clustering", exception.Message);
    }

    [Fact]
    public void RegisteredValidator_MismatchedServiceKeyAndConnectionName_ThrowsWithBothSettingNames()
    {
        using var host = new HostBuilder()
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:ServiceKey"] = "service-key",
                ["Provider:ConnectionName"] = "different-connection",
            }))
            .UseOrleans((context, silo) => new DynamoDBClusteringProviderBuilder().Configure(
                silo,
                name: null,
                context.Configuration.GetSection("Provider")))
            .Build();

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => _ = host.Services.GetRequiredService<IOptions<DynamoDBClusteringOptions>>().Value);

        Assert.Contains("ServiceKey", exception.Message);
        Assert.Contains("ConnectionName", exception.Message);
    }

    [Fact]
    public void RegisteredValidator_AccessKeyWithoutSecretKey_Throws()
    {
        var exception = AssertClusteringValidationFailure(options =>
        {
            options.Service = "us-west-2";
            options.AccessKey = "unpaired-access";
        });

        Assert.Contains(nameof(DynamoDBClusteringOptions.AccessKey), exception.Message);
        Assert.Contains(nameof(DynamoDBClusteringOptions.SecretKey), exception.Message);
        Assert.DoesNotContain("unpaired-access", exception.Message);
    }

    [Fact]
    public void RegisteredValidator_SecretKeyWithoutAccessKey_Throws()
    {
        var exception = AssertGatewayValidationFailure(options =>
        {
            options.Service = "us-west-2";
            options.SecretKey = "unpaired-secret";
        });

        Assert.Contains(nameof(DynamoDBGatewayOptions.AccessKey), exception.Message);
        Assert.Contains(nameof(DynamoDBGatewayOptions.SecretKey), exception.Message);
        Assert.DoesNotContain("unpaired-secret", exception.Message);
    }

    [Fact]
    public void RegisteredValidator_TokenWithoutExplicitPair_Throws()
    {
        var exception = AssertReminderValidationFailure(options =>
        {
            options.Service = "us-west-2";
            options.Token = "unpaired-token";
        });

        Assert.Contains(nameof(DynamoDBReminderStorageOptions.Token), exception.Message);
        Assert.Contains("explicit credentials", exception.Message);
        Assert.DoesNotContain("unpaired-token", exception.Message);
    }

    [Fact]
    public void RegisteredValidator_ExplicitPairAndProfile_ThrowsWithCredentialSettingNames()
    {
        var exception = AssertClusteringValidationFailure(options =>
        {
            options.Service = "us-west-2";
            options.AccessKey = "explicit-access";
            options.SecretKey = "explicit-secret";
            options.ProfileName = "ambiguous-profile";
        });

        Assert.Contains("Explicit credentials", exception.Message);
        Assert.Contains(nameof(DynamoDBClusteringOptions.ProfileName), exception.Message);
        Assert.DoesNotContain("explicit-secret", exception.Message);
    }

    [Fact]
    public void RegisteredValidator_BlankTableName_Throws()
    {
        var exception = AssertStorageValidationFailure(options =>
        {
            options.Service = "us-west-2";
            options.TableName = " ";
        });

        Assert.Contains(nameof(DynamoDBStorageOptions.TableName), exception.Message);
        Assert.Contains("validation-storage", exception.Message);
    }

    [Fact]
    public void RegisteredValidator_ProvisionedReadCapacityIsNonPositive_Throws()
    {
        var exception = AssertClusteringValidationFailure(options =>
        {
            options.Service = "us-west-2";
            options.ReadCapacityUnits = 0;
            options.WriteCapacityUnits = 5;
        });

        Assert.Contains(nameof(DynamoDBClusteringOptions.ReadCapacityUnits), exception.Message);
        Assert.Contains(nameof(DynamoDBClusteringOptions.WriteCapacityUnits), exception.Message);
    }

    [Fact]
    public void RegisteredValidator_ProvisionedWriteCapacityIsNonPositive_Throws()
    {
        var exception = AssertGatewayValidationFailure(options =>
        {
            options.Service = "us-west-2";
            options.ReadCapacityUnits = 10;
            options.WriteCapacityUnits = -1;
        });

        Assert.Contains(nameof(DynamoDBGatewayOptions.ReadCapacityUnits), exception.Message);
        Assert.Contains(nameof(DynamoDBGatewayOptions.WriteCapacityUnits), exception.Message);
    }

    [Fact]
    public void RegisteredValidator_OnDemandCapacityMayBeZero()
    {
        using var host = BuildReminderValidationHost(options =>
        {
            options.Service = "us-west-2";
            options.UseProvisionedThroughput = false;
            options.ReadCapacityUnits = 0;
            options.WriteCapacityUnits = 0;
        });
        var validator = GetValidator(host, "DynamoDBReminderStorageOptionsValidator");

        validator.ValidateConfiguration();

        var options = host.Services.GetRequiredService<IOptions<DynamoDBReminderStorageOptions>>().Value;
        Assert.False(options.UseProvisionedThroughput);
        Assert.Equal(0, options.ReadCapacityUnits);
        Assert.Equal(0, options.WriteCapacityUnits);
    }

    private static OrleansConfigurationException AssertClusteringValidationFailure(
        Action<DynamoDBClusteringOptions> configure)
    {
        using var host = new HostBuilder()
            .UseOrleans(silo => silo.UseDynamoDBClustering((DynamoDBClusteringOptions options) => configure(options)))
            .Build();
        var validator = GetValidator(host, "DynamoDBClusteringOptionsValidator");
        return Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    private static OrleansConfigurationException AssertGatewayValidationFailure(
        Action<DynamoDBGatewayOptions> configure)
    {
        using var host = new HostBuilder()
            .UseOrleansClient(client => client.UseDynamoDBClustering((DynamoDBGatewayOptions options) => configure(options)))
            .Build();
        var validator = GetValidator(host, "DynamoDBGatewayOptionsValidator");
        return Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    private static OrleansConfigurationException AssertStorageValidationFailure(
        Action<DynamoDBStorageOptions> configure)
    {
        using var host = new HostBuilder()
            .UseOrleans(silo => silo.AddDynamoDBGrainStorage(
                "validation-storage",
                (DynamoDBStorageOptions options) =>
                {
                    options.GrainStorageSerializer = ValidationGrainStorageSerializer.Instance;
                    configure(options);
                }))
            .Build();
        var validator = GetValidator(host, nameof(DynamoDBGrainStorageOptionsValidator));
        return Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    private static OrleansConfigurationException AssertReminderValidationFailure(
        Action<DynamoDBReminderStorageOptions> configure)
    {
        using var host = BuildReminderValidationHost(configure);
        var validator = GetValidator(host, "DynamoDBReminderStorageOptionsValidator");
        return Assert.Throws<OrleansConfigurationException>(validator.ValidateConfiguration);
    }

    private static IHost BuildReminderValidationHost(Action<DynamoDBReminderStorageOptions> configure)
        => new HostBuilder()
            .UseOrleans(silo => silo.ConfigureServices(services => services.UseDynamoDBReminderService(configure)))
            .Build();

    private static IConfigurationValidator GetValidator(IHost host, string typeName)
        => host.Services
            .GetServices<IConfigurationValidator>()
            .Single(validator => validator.GetType().Name == typeName);

    private sealed class ValidationGrainStorageSerializer : IGrainStorageSerializer
    {
        public static ValidationGrainStorageSerializer Instance { get; } = new();

        public BinaryData Serialize<T>(T? input) => throw new NotSupportedException();

        public T? Deserialize<T>(BinaryData input) => throw new NotSupportedException();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DynamoDBCredentialEnvironmentCollection
{
    public const string Name = "DynamoDB credential environment";
}

[Collection(DynamoDBCredentialEnvironmentCollection.Name)]
[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Storage")]
[TestCategory("AWS"), TestCategory("DynamoDB"), TestCategory("BVT")]
public sealed class DynamoDBStorageCredentialTests
{
    [Fact]
    public void DynamoDBStorage_DefaultCredentialChainWithRegion_PreservesDefaultChain()
    {
        var storage = new LinkedDynamoDBStorage(
            NullLogger<LinkedDynamoDBStorage>.Instance,
            service: "us-west-2");

        Assert.Null(storage.GetExplicitCredentialsForTest());
        Assert.Equal("us-west-2", storage.ClientForTest.Config.RegionEndpoint.SystemName);
        Assert.Null(storage.ClientForTest.Config.ServiceURL);
    }

    [Fact]
    public void DynamoDBStorage_HttpEmulatorEndpointWithoutCredentials_UsesDummyCredentials()
    {
        var storage = new LinkedDynamoDBStorage(
            NullLogger<LinkedDynamoDBStorage>.Instance,
            service: "http://dynamodb:8000");

        var credentials = Assert.IsType<BasicAWSCredentials>(storage.GetClientCredentialsForTest());
        var immutable = credentials.GetCredentials();

        Assert.Equal("dummy", immutable.AccessKey);
        Assert.Equal("dummyKey", immutable.SecretKey);
        Assert.Equal(new Uri("http://dynamodb:8000").AbsoluteUri, storage.ClientForTest.Config.ServiceURL);
    }

    [Fact]
    public void DynamoDBStorage_HttpsEndpointWithoutCredentials_PreservesDefaultChain()
    {
        var storage = new LinkedDynamoDBStorage(
            NullLogger<LinkedDynamoDBStorage>.Instance,
            service: "https://dynamodb.example");

        Assert.Null(storage.GetClientCredentialsForTest());
        Assert.Equal(new Uri("https://dynamodb.example").AbsoluteUri, storage.ClientForTest.Config.ServiceURL);
    }

    [Fact]
    public void DynamoDBStorage_ExplicitAccessAndSecret_UsesBasicCredentials()
    {
        var storage = new LinkedDynamoDBStorage(
            NullLogger<LinkedDynamoDBStorage>.Instance,
            service: "us-east-2",
            accessKey: "explicit-access",
            secretKey: "explicit-secret");

        var credentials = Assert.IsType<BasicAWSCredentials>(storage.GetExplicitCredentialsForTest());
        var immutable = credentials.GetCredentials();

        Assert.Equal("explicit-access", immutable.AccessKey);
        Assert.Equal("explicit-secret", immutable.SecretKey);
        Assert.Equal(string.Empty, immutable.Token);
        Assert.Equal("us-east-2", storage.ClientForTest.Config.RegionEndpoint.SystemName);
    }

    [Fact]
    public void DynamoDBStorage_ExplicitSessionCredentials_UsesSessionCredentials()
    {
        var storage = new LinkedDynamoDBStorage(
            NullLogger<LinkedDynamoDBStorage>.Instance,
            service: "eu-west-2",
            accessKey: "session-access",
            secretKey: "session-secret",
            token: "session-token");

        var credentials = Assert.IsType<SessionAWSCredentials>(storage.GetExplicitCredentialsForTest());
        var immutable = credentials.GetCredentials();

        Assert.Equal("session-access", immutable.AccessKey);
        Assert.Equal("session-secret", immutable.SecretKey);
        Assert.Equal("session-token", immutable.Token);
        Assert.Equal("eu-west-2", storage.ClientForTest.Config.RegionEndpoint.SystemName);
    }

    [Fact]
    public void DynamoDBStorage_ProfileName_UsesIsolatedSharedCredentialsProfile()
    {
        var profileName = $"phase-one-{Guid.NewGuid():N}";
        var credentialsPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.credentials");
        var expectedAccessKey = $"access-{Guid.NewGuid():N}";
        var expectedSecretKey = $"secret-{Guid.NewGuid():N}";
        var previousProfilesLocation = AWSConfigs.AWSProfilesLocation;
        try
        {
            File.WriteAllText(
                credentialsPath,
                $"[{profileName}]{Environment.NewLine}" +
                $"aws_access_key_id = {expectedAccessKey}{Environment.NewLine}" +
                $"aws_secret_access_key = {expectedSecretKey}{Environment.NewLine}");
            AWSConfigs.AWSProfilesLocation = credentialsPath;

            var storage = new LinkedDynamoDBStorage(
                NullLogger<LinkedDynamoDBStorage>.Instance,
                service: "us-west-2",
                profileName: profileName);
            var credentials = storage.GetExplicitCredentialsForTest();
            var immutable = Assert.IsType<BasicAWSCredentials>(credentials).GetCredentials();
            var exception = Assert.Throws<InvalidOperationException>(() => new LinkedDynamoDBStorage(
                NullLogger<LinkedDynamoDBStorage>.Instance,
                service: "us-west-2",
                profileName: $"{profileName}-missing"));

            Assert.Equal(expectedAccessKey, immutable.AccessKey);
            Assert.Equal(expectedSecretKey, immutable.SecretKey);
            Assert.Equal(string.Empty, immutable.Token);
            Assert.Equal("us-west-2", storage.ClientForTest.Config.RegionEndpoint.SystemName);
            Assert.Contains($"{profileName}-missing", exception.Message);
            Assert.DoesNotContain("aws_secret_access_key", exception.Message);
        }
        finally
        {
            AWSConfigs.AWSProfilesLocation = previousProfilesLocation;
            File.Delete(credentialsPath);
        }
    }
}

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Storage")]
[TestCategory("AWS"), TestCategory("DynamoDB"), TestCategory("BVT")]
public sealed class DynamoDBProviderRegistrationTests
{
    [Fact]
    public void RegisterProviderAttributes_ContainDynamoDBClusteringSiloRegistration()
    {
        var registrations = GetRegistrations(typeof(DynamoDBClusteringOptions).Assembly);

        Assert.Equal(2, registrations.Length);
        AssertRegistration(
            registrations.Single(registration => registration.Target == "Silo"),
            kind: "Clustering",
            target: "Silo",
            typeof(DynamoDBClusteringProviderBuilder));
    }

    [Fact]
    public void RegisterProviderAttributes_ContainDynamoDBClusteringClientRegistration()
    {
        var registrations = GetRegistrations(typeof(DynamoDBGatewayOptions).Assembly);

        Assert.Equal(2, registrations.Length);
        AssertRegistration(
            registrations.Single(registration => registration.Target == "Client"),
            kind: "Clustering",
            target: "Client",
            typeof(DynamoDBClusteringProviderBuilder));
    }

    [Fact]
    public void RegisterProviderAttributes_ContainDynamoDBGrainStorageRegistration()
    {
        var registration = Assert.Single(GetRegistrations(typeof(DynamoDBStorageOptions).Assembly));

        AssertRegistration(
            registration,
            kind: "GrainStorage",
            target: "Silo",
            typeof(DynamoDBGrainStorageProviderBuilder));
    }

    [Fact]
    public void RegisterProviderAttributes_ContainDynamoDBReminderRegistration()
    {
        var registration = Assert.Single(GetRegistrations(typeof(DynamoDBReminderStorageOptions).Assembly));

        AssertRegistration(
            registration,
            kind: "Reminders",
            target: "Silo",
            typeof(DynamoDBRemindersProviderBuilder));
    }

    private static RegisterProviderAttribute[] GetRegistrations(Assembly assembly)
        => assembly.GetCustomAttributes<RegisterProviderAttribute>().ToArray();

    private static void AssertRegistration(
        RegisterProviderAttribute registration,
        string kind,
        string target,
        Type builderType)
    {
        Assert.Equal("DynamoDB", registration.Name);
        Assert.Equal(kind, registration.Kind);
        Assert.Equal(target, registration.Target);
        Assert.Equal(builderType, registration.Type);
    }
}

[TestSuite("BVT")]
[TestProvider("DynamoDB")]
[TestArea("Storage")]
[TestCategory("AWS"), TestCategory("DynamoDB"), TestCategory("BVT")]
public sealed class DynamoDBProviderBuilderTests
{
    [Fact]
    public void ClusteringSiloBuilder_Configure_RegistersMembershipAndBindsOptions()
    {
        using var host = BuildSiloHost(
            new()
            {
                ["Provider:Service"] = "us-west-2",
                ["Provider:TableName"] = "phase-two-silos",
                ["Provider:ReadCapacityUnits"] = "13",
                ["Provider:WriteCapacityUnits"] = "7",
                ["Provider:UseProvisionedThroughput"] = "false",
                ["Provider:CreateIfNotExists"] = "false",
                ["Provider:UpdateIfExists"] = "false",
            },
            (context, silo) => new DynamoDBClusteringProviderBuilder().Configure(
                silo,
                name: null,
                context.Configuration.GetSection("Provider")));

        var membership = host.Services.GetRequiredService<IMembershipTable>();
        var options = host.Services.GetRequiredService<IOptions<DynamoDBClusteringOptions>>().Value;
        var validator = GetValidator(host, "DynamoDBClusteringOptionsValidator");

        Assert.Equal("DynamoDBMembershipTable", membership.GetType().Name);
        Assert.Equal("us-west-2", options.Service);
        Assert.Equal("phase-two-silos", options.TableName);
        Assert.Equal(13, options.ReadCapacityUnits);
        Assert.Equal(7, options.WriteCapacityUnits);
        Assert.False(options.UseProvisionedThroughput);
        Assert.False(options.CreateIfNotExists);
        Assert.False(options.UpdateIfExists);
        validator.ValidateConfiguration();
    }

    [Fact]
    public void ClusteringClientBuilder_Configure_RegistersGatewayAndBindsOptions()
    {
        using var host = BuildClientHost(
            new()
            {
                ["Provider:Service"] = "eu-central-1",
                ["Provider:TableName"] = "phase-two-gateways",
                ["Provider:ReadCapacityUnits"] = "19",
                ["Provider:WriteCapacityUnits"] = "11",
                ["Provider:UseProvisionedThroughput"] = "false",
                ["Provider:CreateIfNotExists"] = "false",
                ["Provider:UpdateIfExists"] = "false",
            },
            (context, client) => new DynamoDBClusteringProviderBuilder().Configure(
                client,
                name: null,
                context.Configuration.GetSection("Provider")));

        var gateway = host.Services.GetRequiredService<Orleans.Messaging.IGatewayListProvider>();
        var options = host.Services.GetRequiredService<IOptions<DynamoDBGatewayOptions>>().Value;
        var validator = GetValidator(host, "DynamoDBGatewayOptionsValidator");

        Assert.Equal("DynamoDBGatewayListProvider", gateway.GetType().Name);
        Assert.Equal("eu-central-1", options.Service);
        Assert.Equal("phase-two-gateways", options.TableName);
        Assert.Equal(19, options.ReadCapacityUnits);
        Assert.Equal(11, options.WriteCapacityUnits);
        Assert.False(options.UseProvisionedThroughput);
        Assert.False(options.CreateIfNotExists);
        Assert.False(options.UpdateIfExists);
        validator.ValidateConfiguration();
    }

    [Fact]
    public void GrainStorageBuilder_Configure_RegistersNamedStorageAndBindsOptions()
    {
        const string providerName = "phase-two-storage";
        const string serializerKey = "phase-two-serializer";
        var serializer = new PhaseTwoGrainStorageSerializer();
        using var host = BuildSiloHost(
            new()
            {
                ["Provider:Service"] = "ap-northeast-1",
                ["Provider:ServiceId"] = "phase-two-service",
                ["Provider:TableName"] = "phase-two-grain-state",
                ["Provider:ReadCapacityUnits"] = "23",
                ["Provider:WriteCapacityUnits"] = "17",
                ["Provider:UseProvisionedThroughput"] = "false",
                ["Provider:CreateIfNotExists"] = "false",
                ["Provider:UpdateIfExists"] = "false",
                ["Provider:DeleteStateOnClear"] = "true",
                ["Provider:TimeToLive"] = "02:03:04",
                ["Provider:SerializerKey"] = serializerKey,
            },
            (context, silo) =>
            {
                silo.ConfigureServices(services =>
                    services.AddKeyedSingleton<IGrainStorageSerializer>(serializerKey, serializer));
                new DynamoDBGrainStorageProviderBuilder().Configure(
                    silo,
                    providerName,
                    context.Configuration.GetSection("Provider"));
            });

        var storage = host.Services.GetRequiredKeyedService<IGrainStorage>(providerName);
        var options = host.Services.GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>().Get(providerName);
        var validator = GetValidator(host, nameof(DynamoDBGrainStorageOptionsValidator));

        Assert.IsType<DynamoDBGrainStorage>(storage);
        Assert.Equal("ap-northeast-1", options.Service);
        Assert.Equal("phase-two-service", options.ServiceId);
        Assert.Equal("phase-two-grain-state", options.TableName);
        Assert.Equal(23, options.ReadCapacityUnits);
        Assert.Equal(17, options.WriteCapacityUnits);
        Assert.False(options.UseProvisionedThroughput);
        Assert.False(options.CreateIfNotExists);
        Assert.False(options.UpdateIfExists);
        Assert.True(options.DeleteStateOnClear);
        Assert.Equal(new TimeSpan(2, 3, 4), options.TimeToLive);
        Assert.Same(serializer, options.GrainStorageSerializer);
        validator.ValidateConfiguration();
    }

    [Fact]
    public void RemindersBuilder_Configure_RegistersReminderTableAndBindsOptions()
    {
        using var host = BuildSiloHost(
            new()
            {
                ["Provider:Service"] = "ca-central-1",
                ["Provider:TableName"] = "phase-two-reminders",
                ["Provider:ReadCapacityUnits"] = "29",
                ["Provider:WriteCapacityUnits"] = "21",
                ["Provider:UseProvisionedThroughput"] = "false",
                ["Provider:CreateIfNotExists"] = "false",
                ["Provider:UpdateIfExists"] = "false",
            },
            (context, silo) => new DynamoDBRemindersProviderBuilder().Configure(
                silo,
                name: null,
                context.Configuration.GetSection("Provider")));

        var reminderTable = host.Services.GetRequiredService<IReminderTable>();
        var options = host.Services.GetRequiredService<IOptions<DynamoDBReminderStorageOptions>>().Value;
        var validator = GetValidator(host, "DynamoDBReminderStorageOptionsValidator");

        Assert.Equal("DynamoDBReminderTable", reminderTable.GetType().Name);
        Assert.Equal("ca-central-1", options.Service);
        Assert.Equal("phase-two-reminders", options.TableName);
        Assert.Equal(29, options.ReadCapacityUnits);
        Assert.Equal(21, options.WriteCapacityUnits);
        Assert.False(options.UseProvisionedThroughput);
        Assert.False(options.CreateIfNotExists);
        Assert.False(options.UpdateIfExists);
        validator.ValidateConfiguration();
    }

    [Theory]
    [InlineData("SiloClustering")]
    [InlineData("ClientClustering")]
    [InlineData("Reminders")]
    public void ProviderBuilder_InvalidLifecycleValue_ThrowsConfigurationException(string target)
    {
        const string invalidValue = "fales";
        var values = new Dictionary<string, string?>
        {
            ["Provider:Service"] = "us-east-1",
            ["Provider:TableName"] = "orleans-table",
            ["Provider:CreateIfNotExists"] = invalidValue,
        };

        using var host = target switch
        {
            "SiloClustering" => BuildSiloHost(
                values,
                (context, silo) => new DynamoDBClusteringProviderBuilder().Configure(
                    silo,
                    name: null,
                    context.Configuration.GetSection("Provider"))),
            "ClientClustering" => BuildClientHost(
                values,
                (context, client) => new DynamoDBClusteringProviderBuilder().Configure(
                    client,
                    name: null,
                    context.Configuration.GetSection("Provider"))),
            "Reminders" => BuildSiloHost(
                values,
                (context, silo) => new DynamoDBRemindersProviderBuilder().Configure(
                    silo,
                    name: null,
                    context.Configuration.GetSection("Provider"))),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
        };

        var exception = Assert.Throws<OrleansConfigurationException>(() =>
        {
            if (target == "SiloClustering")
            {
                _ = host.Services.GetRequiredService<IOptions<DynamoDBClusteringOptions>>().Value;
            }
            else if (target == "ClientClustering")
            {
                _ = host.Services.GetRequiredService<IOptions<DynamoDBGatewayOptions>>().Value;
            }
            else
            {
                _ = host.Services.GetRequiredService<IOptions<DynamoDBReminderStorageOptions>>().Value;
            }
        });

        Assert.Contains("Provider", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DynamoDBClusteringOptions.CreateIfNotExists), exception.Message, StringComparison.Ordinal);
        Assert.Contains(invalidValue, exception.Message, StringComparison.Ordinal);
    }

    private static IHost BuildSiloHost(
        Dictionary<string, string?> values,
        Action<HostBuilderContext, ISiloBuilder> configure)
        => new HostBuilder()
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values))
            .UseOrleans(configure)
            .Build();

    private static IHost BuildClientHost(
        Dictionary<string, string?> values,
        Action<HostBuilderContext, IClientBuilder> configure)
        => new HostBuilder()
            .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values))
            .UseOrleansClient(configure)
            .Build();

    private static IConfigurationValidator GetValidator(IHost host, string typeName)
        => host.Services
            .GetServices<IConfigurationValidator>()
            .Single(validator => validator.GetType().Name == typeName);

    private sealed class PhaseTwoGrainStorageSerializer : IGrainStorageSerializer
    {
        public BinaryData Serialize<T>(T? input) => throw new NotSupportedException();

        public T? Deserialize<T>(BinaryData input) => throw new NotSupportedException();
    }
}
