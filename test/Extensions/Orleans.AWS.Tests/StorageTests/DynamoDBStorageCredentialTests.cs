using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.AWSUtils.Tests;
using Xunit;

namespace AWSUtils.Tests.StorageTests;

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
    [Theory]
    [InlineData("us-west-2")]
    [InlineData("https://dynamodb.example")]
    [InlineData("HTTPS://dynamodb.example")]
    public void DynamoDBStorage_WithoutExplicitCredentials_PreservesDefaultChain(string service)
    {
        var storage = new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, service);
        using var client = storage.ClientForTest;

        Assert.Null(storage.ExplicitCredentialsForTest);
        Assert.IsType<DefaultAWSCredentialsIdentityResolver>(
            client.Config.IdentityResolverConfiguration.GetIdentityResolver<AWSCredentials>());
        AssertService(storage, service);
    }

    [Theory]
    [InlineData("http://dynamodb:8000")]
    [InlineData("HTTP://dynamodb:8000")]
    public void DynamoDBStorage_HttpEmulatorEndpointWithoutCredentials_UsesDummyCredentials(string service)
    {
        var storage = new DynamoDBStorage(NullLogger<DynamoDBStorage>.Instance, service);
        using var client = storage.ClientForTest;

        var credentials = Assert.IsType<BasicAWSCredentials>(storage.ExplicitCredentialsForTest).GetCredentials();

        Assert.Equal("dummy", credentials.AccessKey);
        Assert.Equal("dummyKey", credentials.SecretKey);
        Assert.Equal(string.Empty, credentials.Token);
        AssertService(storage, service);
    }

    [Theory]
    [InlineData("us-east-2")]
    [InlineData("http://dynamodb:8000")]
    [InlineData("https://dynamodb.example")]
    public void DynamoDBStorage_ExplicitAccessAndSecret_UsesBasicCredentials(string service)
    {
        var storage = new DynamoDBStorage(
            NullLogger<DynamoDBStorage>.Instance,
            service,
            accessKey: "explicit-access",
            secretKey: "explicit-secret",
            profileName: $"unused-{Guid.NewGuid():N}");
        using var client = storage.ClientForTest;

        var credentials = Assert.IsType<BasicAWSCredentials>(storage.ExplicitCredentialsForTest).GetCredentials();

        Assert.Equal("explicit-access", credentials.AccessKey);
        Assert.Equal("explicit-secret", credentials.SecretKey);
        Assert.Equal(string.Empty, credentials.Token);
        AssertService(storage, service);
    }

    [Theory]
    [InlineData("eu-west-2")]
    [InlineData("http://dynamodb:8000")]
    [InlineData("https://dynamodb.example")]
    public void DynamoDBStorage_ExplicitSessionCredentials_UsesSessionCredentials(string service)
    {
        var storage = new DynamoDBStorage(
            NullLogger<DynamoDBStorage>.Instance,
            service,
            accessKey: "session-access",
            secretKey: "session-secret",
            token: "session-token",
            profileName: $"unused-{Guid.NewGuid():N}");
        using var client = storage.ClientForTest;

        var credentials = Assert.IsType<SessionAWSCredentials>(storage.ExplicitCredentialsForTest).GetCredentials();

        Assert.Equal("session-access", credentials.AccessKey);
        Assert.Equal("session-secret", credentials.SecretKey);
        Assert.Equal("session-token", credentials.Token);
        AssertService(storage, service);
    }

    [Theory]
    [InlineData("us-west-2", false)]
    [InlineData("http://dynamodb:8000", false)]
    [InlineData("https://dynamodb.example", false)]
    [InlineData("us-west-2", true)]
    [InlineData("http://dynamodb:8000", true)]
    [InlineData("https://dynamodb.example", true)]
    public void DynamoDBStorage_ProfileName_UsesIsolatedSharedCredentialsProfile(string service, bool useSessionToken)
    {
        var profileName = $"dynamodb-credentials-{Guid.NewGuid():N}";
        var credentialsPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.credentials");
        var previousProfilesLocation = AWSConfigs.AWSProfilesLocation;
        try
        {
            File.WriteAllText(
                credentialsPath,
                $"[{profileName}]{Environment.NewLine}" +
                $"aws_access_key_id = profile-access{Environment.NewLine}" +
                $"aws_secret_access_key = profile-secret{Environment.NewLine}" +
                (useSessionToken ? $"aws_session_token = profile-token{Environment.NewLine}" : string.Empty));
            AWSConfigs.AWSProfilesLocation = credentialsPath;

            var storage = new DynamoDBStorage(
                NullLogger<DynamoDBStorage>.Instance,
                service,
                profileName: profileName);
            using var client = storage.ClientForTest;
            var credentials = useSessionToken
                ? Assert.IsType<SessionAWSCredentials>(storage.ExplicitCredentialsForTest).GetCredentials()
                : Assert.IsType<BasicAWSCredentials>(storage.ExplicitCredentialsForTest).GetCredentials();

            Assert.Equal("profile-access", credentials.AccessKey);
            Assert.Equal("profile-secret", credentials.SecretKey);
            Assert.Equal(useSessionToken ? "profile-token" : string.Empty, credentials.Token);
            AssertService(storage, service);

            var missingProfileName = $"{profileName}-missing";
            var exception = Assert.Throws<InvalidOperationException>(() => new DynamoDBStorage(
                NullLogger<DynamoDBStorage>.Instance,
                service,
                profileName: missingProfileName));

            Assert.Equal(
                $"AWS named profile '{missingProfileName}' provided, but credentials could not be retrieved",
                exception.Message);
        }
        finally
        {
            AWSConfigs.AWSProfilesLocation = previousProfilesLocation;
            File.Delete(credentialsPath);
        }
    }

    private static void AssertService(DynamoDBStorage storage, string service)
    {
        var config = storage.ClientForTest.Config;
        if (Uri.TryCreate(service, UriKind.Absolute, out var uri))
        {
            Assert.Equal(uri, new Uri(config.ServiceURL));
            Assert.Null(config.RegionEndpoint);
        }
        else
        {
            Assert.Equal(service, config.RegionEndpoint.SystemName);
            Assert.Null(config.ServiceURL);
        }
    }
}
