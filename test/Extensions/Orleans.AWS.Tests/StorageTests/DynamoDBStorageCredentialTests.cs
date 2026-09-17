using System.Net;
using System.Net.Http.Headers;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
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
    [InlineData("us-east-2", "us-east-2")]
    [InlineData("http://dynamodb:8000", "us-east-1")]
    [InlineData("https://dynamodb.example", "us-east-1")]
    [InlineData("https://dynamodb.us-west-2.amazonaws.com", "us-west-2")]
    [InlineData("https://dynamodb.eu-west-2.amazonaws.com", "eu-west-2")]
    [InlineData("https://dynamodb.cn-north-1.amazonaws.com.cn", "cn-north-1")]
    [InlineData("https://dynamodb-fips.us-gov-west-1.amazonaws.com", "us-gov-west-1")]
    public async Task DynamoDBStorage_ExplicitAccessAndSecret_UsesBasicCredentials(string service, string signingRegion)
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
        await AssertRequestSigningAsync(storage, signingRegion, "explicit-access", token: null);
    }

    [Theory]
    [InlineData("eu-west-2", "eu-west-2")]
    [InlineData("http://dynamodb:8000", "us-east-1")]
    [InlineData("https://dynamodb.example", "us-east-1")]
    [InlineData("https://dynamodb.us-west-2.amazonaws.com", "us-west-2")]
    [InlineData("https://dynamodb.eu-west-2.amazonaws.com", "eu-west-2")]
    [InlineData("https://dynamodb.cn-north-1.amazonaws.com.cn", "cn-north-1")]
    [InlineData("https://dynamodb-fips.us-gov-west-1.amazonaws.com", "us-gov-west-1")]
    public async Task DynamoDBStorage_ExplicitSessionCredentials_UsesSessionCredentials(string service, string signingRegion)
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
        await AssertRequestSigningAsync(storage, signingRegion, "session-access", "session-token");
    }

    [Theory]
    [InlineData("us-west-2", false, "us-west-2")]
    [InlineData("http://dynamodb:8000", false, "us-east-1")]
    [InlineData("https://dynamodb.example", false, "us-east-1")]
    [InlineData("https://dynamodb.us-west-2.amazonaws.com", false, "us-west-2")]
    [InlineData("https://dynamodb.eu-west-2.amazonaws.com", false, "eu-west-2")]
    [InlineData("https://dynamodb.cn-north-1.amazonaws.com.cn", false, "cn-north-1")]
    [InlineData("https://dynamodb-fips.us-gov-west-1.amazonaws.com", false, "us-gov-west-1")]
    [InlineData("us-west-2", true, "us-west-2")]
    [InlineData("http://dynamodb:8000", true, "us-east-1")]
    [InlineData("https://dynamodb.example", true, "us-east-1")]
    [InlineData("https://dynamodb.us-west-2.amazonaws.com", true, "us-west-2")]
    [InlineData("https://dynamodb.eu-west-2.amazonaws.com", true, "eu-west-2")]
    [InlineData("https://dynamodb.cn-north-1.amazonaws.com.cn", true, "cn-north-1")]
    [InlineData("https://dynamodb-fips.us-gov-west-1.amazonaws.com", true, "us-gov-west-1")]
    public async Task DynamoDBStorage_ProfileName_UsesIsolatedSharedCredentialsProfile(string service, bool useSessionToken, string signingRegion)
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
            await AssertRequestSigningAsync(storage, signingRegion, "profile-access", useSessionToken ? "profile-token" : null);

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

    private static async Task AssertRequestSigningAsync(DynamoDBStorage storage, string region, string accessKey, string? token)
    {
        using var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var config = Assert.IsType<AmazonDynamoDBConfig>(storage.ClientForTest.Config);
        config.HttpClientFactory = new TestHttpClientFactory(httpClient);

        await storage.ClientForTest.ListTablesAsync(new ListTablesRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.RequestCount);
        Assert.NotNull(handler.Authorization);
        Assert.NotNull(handler.RequestDate);
        Assert.StartsWith(
            $"AWS4-HMAC-SHA256 Credential={accessKey}/{handler.RequestDate[..8]}/{region}/dynamodb/aws4_request,",
            handler.Authorization);
        Assert.Equal(token, handler.SessionToken);
        if (config.ServiceURL is { } endpoint)
        {
            Assert.NotNull(handler.RequestUri);
            Assert.Equal(new Uri(endpoint).AbsoluteUri, handler.RequestUri.AbsoluteUri);
        }
    }

    private sealed class TestHttpClientFactory(HttpClient client) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => client;
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public string? RequestDate { get; private set; }
        public string? SessionToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUri = request.RequestUri;
            Authorization = Assert.Single(request.Headers.GetValues("Authorization"));
            RequestDate = Assert.Single(request.Headers.GetValues("X-Amz-Date"));
            SessionToken = request.Headers.TryGetValues("X-Amz-Security-Token", out var tokens) ? Assert.Single(tokens) : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"TableNames":[]}""", new MediaTypeHeaderValue("application/x-amz-json-1.0")),
            });
        }
    }
}
