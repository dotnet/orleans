using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Kinesis;
using Amazon.Kinesis.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using TestExtensions;
using Xunit;

namespace Orleans.Streaming.Kinesis.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KinesisCredentialEnvironmentCollection
{
    public const string Name = "Kinesis credential environment";
}

[Collection(KinesisCredentialEnvironmentCollection.Name)]
[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("Kinesis")]
[TestCategory("AWS"), TestCategory("Kinesis")]
public sealed class KinesisEndpointCredentialTests
{
    private static readonly PropertyInfo ExplicitCredentialsProperty = typeof(AmazonServiceClient)
        .GetProperty("ExplicitAWSCredentials", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(false, "http://kinesis:4566")]
    [InlineData(false, "http://localhost:4566")]
    [InlineData(false, "HTTP://kinesis:4566")]
    [InlineData(true, "http://dynamodb:8000")]
    [InlineData(true, "http://localhost:8000")]
    [InlineData(true, "HTTP://dynamodb:8000")]
    [InlineData(true, "https://localhost:8000")]
    public async Task LocalEndpoints_UseDevelopmentCredentials(bool checkpoint, string service)
    {
        using var client = CreateClient(checkpoint, service);
        var credentials = Assert.IsType<BasicAWSCredentials>(GetExplicitCredentials(client)).GetCredentials();

        Assert.Equal("dummy", credentials.AccessKey);
        Assert.Equal("dummy", credentials.SecretKey);
        Assert.Equal(string.Empty, credentials.Token);
        Assert.Equal(new Uri(service), new Uri(client.Config.ServiceURL));
        await AssertSignedRequest(client, "dummy", null);
    }

    [Theory]
    [InlineData(false, "http://kinesis:4566", null)]
    [InlineData(false, "https://kinesis.example", null)]
    [InlineData(false, "us-east-1", null)]
    [InlineData(true, "http://dynamodb:8000", null)]
    [InlineData(true, "http://dynamodb:8000", "explicit-token")]
    [InlineData(true, "https://dynamodb.example", "explicit-token")]
    [InlineData(true, "us-east-1", "explicit-token")]
    public async Task ExplicitCredentialsOverrideEndpointDefaults(bool checkpoint, string service, string? token)
    {
        using var client = CreateClient(checkpoint, service, "explicit-access", "explicit-secret", token);
        var credentials = Assert.IsAssignableFrom<AWSCredentials>(GetExplicitCredentials(client)).GetCredentials();

        Assert.Equal("explicit-access", credentials.AccessKey);
        Assert.Equal("explicit-secret", credentials.SecretKey);
        Assert.Equal(token ?? string.Empty, credentials.Token);
        await AssertSignedRequest(client, "explicit-access", token);
    }

    [Theory]
    [InlineData(false, "https://kinesis.example")]
    [InlineData(false, "https://localhost:4566")]
    [InlineData(false, "us-east-1")]
    [InlineData(true, "https://dynamodb.example")]
    [InlineData(true, "us-east-1")]
    public void SecureEndpointsAndRegions_PreserveDefaultCredentialChain(bool checkpoint, string service)
    {
        using var client = CreateClient(checkpoint, service);

        Assert.Null(GetExplicitCredentials(client));
        if (Uri.TryCreate(service, UriKind.Absolute, out var uri))
        {
            Assert.Equal(uri, new Uri(client.Config.ServiceURL));
        }
        else
        {
            Assert.Equal(service, client.Config.RegionEndpoint.SystemName);
        }
    }

    [Theory]
    [InlineData("http://dynamodb:8000")]
    [InlineData("https://dynamodb.example")]
    [InlineData("us-east-1")]
    public async Task CheckpointProfileOverridesEndpointDefaults(string service)
    {
        var path = Path.GetTempFileName();
        var profileName = $"kinesis-checkpoints-{Guid.NewGuid():N}";
        var previousProfilesLocation = AWSConfigs.AWSProfilesLocation;
        try
        {
            await File.WriteAllTextAsync(
                path,
                $"[{profileName}]\naws_access_key_id=profile-access\naws_secret_access_key=profile-secret\naws_session_token=profile-token\n",
                TestContext.Current.CancellationToken);
            AWSConfigs.AWSProfilesLocation = path;
            using var client = CreateClient(checkpoint: true, service, profileName: profileName);
            var credentials = Assert.IsAssignableFrom<AWSCredentials>(GetExplicitCredentials(client)).GetCredentials();

            Assert.Equal("profile-access", credentials.AccessKey);
            Assert.Equal("profile-secret", credentials.SecretKey);
            Assert.Equal("profile-token", credentials.Token);
            await AssertSignedRequest(client, "profile-access", "profile-token");

            var missingProfile = $"{profileName}-missing";
            var exception = Assert.Throws<InvalidOperationException>(() =>
                CreateClient(checkpoint: true, service, profileName: missingProfile));
            Assert.Contains(missingProfile, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("profile-secret", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            AWSConfigs.AWSProfilesLocation = previousProfilesLocation;
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AspireContainerEndpoints_CreateAuthenticatedClients()
    {
        const string providerName = "orders";
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Orleans:Streaming:{providerName}:ProviderType"] = "Kinesis",
                [$"Orleans:Streaming:{providerName}:StreamName"] = "orders",
                [$"Orleans:Streaming:{providerName}:Region"] = "us-east-1",
                [$"Orleans:Streaming:{providerName}:Checkpoint:Type"] = "DynamoDB",
                ["AWS_ENDPOINT_URL_KINESIS"] = "http://kinesis:4566",
                ["AWS_ENDPOINT_URL_DYNAMODB"] = "http://dynamodb:8000",
            }))
            .UseOrleans(silo => silo.UseLocalhostClustering())
            .Build();
        var kinesisOptions = host.Services.GetRequiredService<IOptionsMonitor<KinesisStreamOptions>>().Get(providerName);
        var checkpointOptions = host.Services.GetRequiredService<IOptionsMonitor<DynamoDBStreamQueueCheckpointerOptions>>().Get(providerName);
        using var kinesis = Assert.IsType<AmazonKinesisClient>(KinesisAdapterFactory.CreateClient(kinesisOptions));
        using var checkpoint = Assert.IsType<AmazonDynamoDBClient>(DynamoDBStreamQueueCheckpointerFactory.CreateClient(checkpointOptions));

        Assert.Equal("http://kinesis:4566", kinesisOptions.Service);
        Assert.Equal("http://dynamodb:8000", checkpointOptions.Service);
        Assert.IsType<BasicAWSCredentials>(GetExplicitCredentials(kinesis));
        Assert.IsType<BasicAWSCredentials>(GetExplicitCredentials(checkpoint));
        await AssertSignedRequest(kinesis, "dummy", null);
        await AssertSignedRequest(checkpoint, "dummy", null);
    }

    private static AmazonServiceClient CreateClient(
        bool checkpoint,
        string service,
        string? accessKey = null,
        string? secretKey = null,
        string? token = null,
        string? profileName = null)
        => checkpoint
            ? Assert.IsType<AmazonDynamoDBClient>(DynamoDBStreamQueueCheckpointerFactory.CreateClient(new()
            {
                Service = service,
                AccessKey = accessKey,
                SecretKey = secretKey,
                Token = token,
                ProfileName = profileName,
            }))
            : Assert.IsType<AmazonKinesisClient>(KinesisAdapterFactory.CreateClient(new()
            {
                Service = service,
                Region = "us-east-1",
                AccessKey = accessKey,
                SecretKey = secretKey,
            }));

    private static AWSCredentials? GetExplicitCredentials(AmazonServiceClient client)
        => (AWSCredentials?)ExplicitCredentialsProperty.GetValue(client);

    private static async Task AssertSignedRequest(AmazonServiceClient client, string accessKey, string? token)
    {
        var checkpoint = client is AmazonDynamoDBClient;
        using var handler = new RecordingHandler(
            checkpoint ? """{"TableNames":[]}""" : """{"Shards":[]}""",
            checkpoint ? "application/x-amz-json-1.0" : "application/x-amz-json-1.1");
        using var httpClient = new HttpClient(handler);
        var config = Assert.IsAssignableFrom<ClientConfig>(client.Config);
        config.HttpClientFactory = new TestHttpClientFactory(httpClient);
        if (client is IAmazonKinesis kinesis)
        {
            await kinesis.ListShardsAsync(new ListShardsRequest { StreamName = "orders" }, TestContext.Current.CancellationToken);
        }
        else
        {
            await Assert.IsAssignableFrom<IAmazonDynamoDB>(client).ListTablesAsync(new ListTablesRequest(), TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, handler.RequestCount);
        Assert.NotNull(handler.Authorization);
        Assert.NotNull(handler.RequestDate);
        var service = checkpoint ? "dynamodb" : "kinesis";
        Assert.StartsWith(
            $"AWS4-HMAC-SHA256 Credential={accessKey}/{handler.RequestDate[..8]}/us-east-1/{service}/aws4_request,",
            handler.Authorization,
            StringComparison.Ordinal);
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

    private sealed class RecordingHandler(string response, string contentType) : HttpMessageHandler
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
                Content = new StringContent(response, new MediaTypeHeaderValue(contentType)),
            });
        }
    }
}
