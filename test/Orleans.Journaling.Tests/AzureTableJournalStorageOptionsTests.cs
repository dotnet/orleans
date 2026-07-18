using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Xunit;

namespace Orleans.Journaling.Tests;

[TestCategory("BVT")]
public sealed class AzureTableJournalStorageOptionsTests
{
    private static readonly Uri ServiceUri = new("https://account.table.example/");
    private static readonly Uri ServiceUriWithSas = new(
        "https://account.table.example/?sv=2026-01-01&ss=t&srt=s&sp=r&se=2030-01-01T00%3A00%3A00Z&sig=signature");

    [Fact]
    public void Constructor_UsesDocumentedDefaults()
    {
        var options = new AzureTableJournalStorageOptions();
        var journalId = new JournalId("tenant/journal");

        Assert.Equal(AzureTableJournalStorageOptions.DEFAULT_TABLE_NAME, options.TableName);
        Assert.Equal("journal", options.TableName);
        Assert.Equal("tenant%2Fjournal", options.GetPartitionKey(journalId));
        Assert.Null(options.ClientOptions);
        Assert.Null(options.TableServiceClient);
        Assert.True(options.DeleteOldGenerations);
        Assert.Equal(10_000, options.CompactionRowCountThreshold);
        Assert.Equal(AzureTableJournalStorageOptions.DEFAULT_COMPACTION_ROW_COUNT_THRESHOLD, options.CompactionRowCountThreshold);
        Assert.Equal(32L * 1024 * 1024, options.CompactionSizeThreshold);
        Assert.Equal(AzureTableJournalStorageOptions.DEFAULT_COMPACTION_SIZE_THRESHOLD, options.CompactionSizeThreshold);
        Assert.Equal(5, options.MaxMetadataOnlyConflictRetries);
        Assert.Equal(AzureTableJournalStorageOptions.DEFAULT_MAX_METADATA_ONLY_CONFLICT_RETRIES, options.MaxMetadataOnlyConflictRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(10), options.MetadataOnlyConflictInitialBackoff);
        Assert.Equal(AzureTableJournalStorageOptions.DEFAULT_METADATA_ONLY_CONFLICT_INITIAL_BACKOFF, options.MetadataOnlyConflictInitialBackoff);
        Assert.Equal(TimeSpan.FromMilliseconds(200), options.MetadataOnlyConflictMaxBackoff);
        Assert.Equal(AzureTableJournalStorageOptions.DEFAULT_METADATA_ONLY_CONFLICT_MAX_BACKOFF, options.MetadataOnlyConflictMaxBackoff);
        Assert.Null(options.CreateClient);
    }

    [Theory]
    [InlineData("simple-._~09AZaz", "simple-._~09AZaz")]
    [InlineData("parent/child", "parent%2Fchild")]
    [InlineData("slash\\hash#question?", "slash%5Chash%23question%3F")]
    [InlineData("control\0\u0001\u001F\u007F", "control%00%01%1F%7F")]
    [InlineData("space + percent%", "space%20%2B%20percent%25")]
    [InlineData("café/😀", "caf%C3%A9%2F%F0%9F%98%80")]
    public void GetDefaultPartitionKey_PercentEncodesJournalIdValue(string value, string expected)
    {
        var partitionKey = AzureTableJournalStorageOptions.GetDefaultPartitionKey(new JournalId(value));

        Assert.Equal(expected, partitionKey);
        Assert.Equal(value, Uri.UnescapeDataString(partitionKey));
        Assert.DoesNotContain(partitionKey, static character => character is '/' or '\\' or '#' or '?' or '\0');
    }

    [Fact]
    public void GetDefaultPartitionKey_DefaultJournalId_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => AzureTableJournalStorageOptions.GetDefaultPartitionKey(default));

        Assert.Equal("journalId", exception.ParamName);
        Assert.Contains("must not be the default value", exception.Message);
    }

    [Fact]
    public void GetPartitionKeyForJournal_CustomMapper_ReceivesJournalIdAndReturnsMappedValue()
    {
        var expectedJournalId = new JournalId("tenant/journal");
        JournalId receivedJournalId = default;
        var invocationCount = 0;
        var options = new AzureTableJournalStorageOptions
        {
            GetPartitionKey = journalId =>
            {
                invocationCount++;
                receivedJournalId = journalId;
                return $"partition::{Uri.EscapeDataString(journalId.Value)}";
            },
        };

        var partitionKey = options.GetPartitionKeyForJournal(expectedJournalId);

        Assert.Equal("partition::tenant%2Fjournal", partitionKey);
        Assert.Equal(expectedJournalId, receivedJournalId);
        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void GetPartitionKeyForJournal_DefaultJournalId_RejectsBeforeInvokingMapper()
    {
        var mapperInvoked = false;
        var options = new AzureTableJournalStorageOptions
        {
            GetPartitionKey = _ =>
            {
                mapperInvoked = true;
                return "partition";
            },
        };

        var exception = Assert.Throws<ArgumentException>(
            () => options.GetPartitionKeyForJournal(default));

        Assert.Equal("journalId", exception.ParamName);
        Assert.False(mapperInvoked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void GetPartitionKeyForJournal_MapperReturnsInvalidValue_Throws(string? mappedValue)
    {
        var options = new AzureTableJournalStorageOptions
        {
            GetPartitionKey = _ => mappedValue!,
        };

        var exception = Assert.ThrowsAny<ArgumentException>(
            () => options.GetPartitionKeyForJournal(new JournalId("journal")));

        Assert.Equal("partitionKey", exception.ParamName);
    }

    [Fact]
    public void GetPartitionKeyForJournal_NullMapper_ThrowsClearError()
    {
        var options = new AzureTableJournalStorageOptions { GetPartitionKey = null! };

        var exception = Assert.Throws<ArgumentNullException>(
            () => options.GetPartitionKeyForJournal(new JournalId("journal")));

        Assert.Equal(nameof(AzureTableJournalStorageOptions.GetPartitionKey), exception.ParamName);
    }

    [Theory]
    [InlineData("invalid/key")]
    [InlineData("invalid\\key")]
    [InlineData("invalid#key")]
    [InlineData("invalid?key")]
    [InlineData("invalid\u0001key")]
    public void GetPartitionKeyForJournal_CustomMapperReturnsInvalidAzureKey_Throws(string partitionKey)
    {
        var options = new AzureTableJournalStorageOptions { GetPartitionKey = _ => partitionKey };

        var exception = Assert.Throws<ArgumentException>(
            () => options.GetPartitionKeyForJournal(new JournalId("journal")));

        Assert.Equal("partitionKey", exception.ParamName);
    }

    [Fact]
    public void GetDefaultPartitionKey_EncodedValueExceedsAzureLimit_ThrowsLocally()
    {
        var journalId = new JournalId(new string('/', 342));

        var exception = Assert.Throws<ArgumentException>(
            () => AzureTableJournalStorageOptions.GetDefaultPartitionKey(journalId));

        Assert.Equal("journalId", exception.ParamName);
        Assert.Contains("1,024", exception.Message);
    }

    [Fact]
    public async Task ConfigureTableServiceClient_ConnectionString_CreatesClientForConfiguredEndpoint()
    {
        var options = new AzureTableJournalStorageOptions();
        var accountKey = Convert.ToBase64String(new byte[32]);
        var connectionString = $"DefaultEndpointsProtocol=https;AccountName=account;AccountKey={accountKey};TableEndpoint={ServiceUri}";

        options.ConfigureTableServiceClient(connectionString);

        await AssertCreatesNewClientAtServiceUri(options);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void ConfigureTableServiceClient_ConnectionStringIsInvalid_Throws(string? connectionString)
    {
        var options = new AzureTableJournalStorageOptions();

        var exception = Assert.ThrowsAny<ArgumentException>(
            () => options.ConfigureTableServiceClient(connectionString!));

        Assert.Equal("connectionString", exception.ParamName);
        Assert.Null(options.CreateClient);
    }

    [Fact]
    public async Task ConfigureTableServiceClient_ClientOptions_AreAppliedToCreatedClient()
    {
        var policy = new ThrowingPipelinePolicy();
        var clientOptions = new TableClientOptions();
        clientOptions.AddPolicy(policy, HttpPipelinePosition.PerCall);
        var options = new AzureTableJournalStorageOptions
        {
            ClientOptions = clientOptions,
        };
        var accountKey = Convert.ToBase64String(new byte[32]);
        var connectionString = $"DefaultEndpointsProtocol=https;AccountName=account;AccountKey={accountKey};TableEndpoint={ServiceUri}";
        options.ConfigureTableServiceClient(connectionString);
        var client = await options.CreateClient!(CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetPropertiesAsync());

        Assert.Equal(ThrowingPipelinePolicy.ExceptionMessage, exception.Message);
        Assert.Equal(1, policy.InvocationCount);
        Assert.Equal(RequestMethod.Get, policy.RequestMethod);
    }

    [Fact]
    public async Task ConfigureTableServiceClient_ServiceUri_CreatesClientForConfiguredEndpoint()
    {
        var options = new AzureTableJournalStorageOptions();

        options.ConfigureTableServiceClient(ServiceUriWithSas);

        await AssertCreatesNewClientAtServiceUri(options);
    }

    [Fact]
    public void ConfigureTableServiceClient_ServiceUriIsNull_Throws()
    {
        var options = new AzureTableJournalStorageOptions();

        var exception = Assert.Throws<ArgumentNullException>(
            () => options.ConfigureTableServiceClient((Uri)null!));

        Assert.Equal("serviceUri", exception.ParamName);
        Assert.Null(options.CreateClient);
    }

    [Fact]
    public async Task ConfigureTableServiceClient_Callback_ReturnsCallbackResultAndForwardsCancellationToken()
    {
        var expectedClient = new TableServiceClient(ServiceUri, CreateSharedKeyCredential());
        using var cancellation = new CancellationTokenSource();
        CancellationToken receivedToken = default;
        var invocationCount = 0;
        var options = new AzureTableJournalStorageOptions();
        options.ConfigureTableServiceClient(token =>
        {
            invocationCount++;
            receivedToken = token;
            return Task.FromResult(expectedClient);
        });

        var actualClient = await options.CreateClient!(cancellation.Token);

        Assert.Same(expectedClient, actualClient);
        Assert.Equal(cancellation.Token, receivedToken);
        Assert.Equal(1, invocationCount);
        Assert.Null(options.TableServiceClient);
    }

    [Fact]
    public async Task ConfigureTableServiceClient_CallbackCancellation_IsPropagatedWithOriginalToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var options = new AzureTableJournalStorageOptions();
        options.ConfigureTableServiceClient(
            token => Task.FromCanceled<TableServiceClient>(token));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => options.CreateClient!(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public void ConfigureTableServiceClient_CallbackIsNull_Throws()
    {
        var options = new AzureTableJournalStorageOptions();

        var exception = Assert.Throws<ArgumentNullException>(
            () => options.ConfigureTableServiceClient(
                (Func<CancellationToken, Task<TableServiceClient>>)null!));

        Assert.Equal("createClientCallback", exception.ParamName);
        Assert.Null(options.CreateClient);
    }

    [Fact]
    public async Task ConfigureTableServiceClient_TokenCredential_CreatesClientForConfiguredEndpoint()
    {
        var options = new AzureTableJournalStorageOptions();

        options.ConfigureTableServiceClient(ServiceUri, new StubTokenCredential());

        await AssertCreatesNewClientAtServiceUri(options);
    }

    [Fact]
    public void ConfigureTableServiceClient_TokenCredentialAndNullServiceUri_Throws()
    {
        var options = new AzureTableJournalStorageOptions();

        var exception = Assert.Throws<ArgumentNullException>(
            () => options.ConfigureTableServiceClient(null!, new StubTokenCredential()));

        Assert.Equal("serviceUri", exception.ParamName);
        Assert.Null(options.CreateClient);
    }

    [Fact]
    public void ConfigureTableServiceClient_NullTokenCredential_Throws()
    {
        var options = new AzureTableJournalStorageOptions();

        var exception = Assert.Throws<ArgumentNullException>(
            () => options.ConfigureTableServiceClient(ServiceUri, (TokenCredential)null!));

        Assert.Equal("tokenCredential", exception.ParamName);
        Assert.Null(options.CreateClient);
    }

    [Fact]
    public async Task ConfigureTableServiceClient_SasCredential_CreatesClientForConfiguredEndpoint()
    {
        var options = new AzureTableJournalStorageOptions();
        var credential = new AzureSasCredential("sv=2026-01-01&ss=t&srt=s&sp=r&se=2030-01-01T00:00:00Z&sig=signature");

        options.ConfigureTableServiceClient(ServiceUri, credential);

        await AssertCreatesNewClientAtServiceUri(options);
    }

    [Fact]
    public void ConfigureTableServiceClient_SasCredentialAndNullServiceUri_Throws()
    {
        var options = new AzureTableJournalStorageOptions();
        var credential = new AzureSasCredential("sig=signature");

        var exception = Assert.Throws<ArgumentNullException>(
            () => options.ConfigureTableServiceClient(null!, credential));

        Assert.Equal("serviceUri", exception.ParamName);
        Assert.Null(options.CreateClient);
    }

    [Fact]
    public void ConfigureTableServiceClient_NullSasCredential_Throws()
    {
        var options = new AzureTableJournalStorageOptions();

        var exception = Assert.Throws<ArgumentNullException>(
            () => options.ConfigureTableServiceClient(ServiceUri, (AzureSasCredential)null!));

        Assert.Equal("azureSasCredential", exception.ParamName);
        Assert.Null(options.CreateClient);
    }

    [Fact]
    public async Task ConfigureTableServiceClient_SharedKeyCredential_CreatesClientForConfiguredEndpoint()
    {
        var options = new AzureTableJournalStorageOptions();

        options.ConfigureTableServiceClient(ServiceUri, CreateSharedKeyCredential());

        await AssertCreatesNewClientAtServiceUri(options);
    }

    [Fact]
    public void ConfigureTableServiceClient_SharedKeyCredentialAndNullServiceUri_Throws()
    {
        var options = new AzureTableJournalStorageOptions();

        var exception = Assert.Throws<ArgumentNullException>(
            () => options.ConfigureTableServiceClient(null!, CreateSharedKeyCredential()));

        Assert.Equal("serviceUri", exception.ParamName);
        Assert.Null(options.CreateClient);
    }

    [Fact]
    public void ConfigureTableServiceClient_NullSharedKeyCredential_Throws()
    {
        var options = new AzureTableJournalStorageOptions();

        var exception = Assert.Throws<ArgumentNullException>(
            () => options.ConfigureTableServiceClient(ServiceUri, (TableSharedKeyCredential)null!));

        Assert.Equal("sharedKeyCredential", exception.ParamName);
        Assert.Null(options.CreateClient);
    }

    [Fact]
    public async Task TableServiceClient_Set_ConfiguresFactoryToReturnSameClient()
    {
        var expectedClient = new TableServiceClient(ServiceUri, CreateSharedKeyCredential());
        var options = new AzureTableJournalStorageOptions
        {
            TableServiceClient = expectedClient,
        };

        var first = await options.CreateClient!(CancellationToken.None);
        var second = await options.CreateClient(CancellationToken.None);

        Assert.Same(expectedClient, options.TableServiceClient);
        Assert.Same(expectedClient, first);
        Assert.Same(first, second);
    }

    [Fact]
    public void TableServiceClient_SetNull_Throws()
    {
        var options = new AzureTableJournalStorageOptions();

        var exception = Assert.Throws<ArgumentNullException>(
            () => options.TableServiceClient = null!);

        Assert.Equal("value", exception.ParamName);
        Assert.Null(options.TableServiceClient);
        Assert.Null(options.CreateClient);
    }

    private static async Task AssertCreatesNewClientAtServiceUri(AzureTableJournalStorageOptions options)
    {
        Assert.NotNull(options.CreateClient);

        var first = await options.CreateClient(CancellationToken.None);
        var second = await options.CreateClient(CancellationToken.None);

        Assert.Equal(ServiceUri, first.Uri);
        Assert.Equal("account", first.AccountName);
        Assert.Equal(ServiceUri, second.Uri);
        Assert.NotSame(first, second);
        Assert.Null(options.TableServiceClient);
    }

    private static TableSharedKeyCredential CreateSharedKeyCredential()
        => new("account", Convert.ToBase64String(new byte[32]));

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Credential use is not expected while constructing a client.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Credential use is not expected while constructing a client.");
    }

    private sealed class ThrowingPipelinePolicy : HttpPipelineSynchronousPolicy
    {
        public const string ExceptionMessage = "The configured client options policy was invoked.";

        public int InvocationCount { get; private set; }

        public RequestMethod RequestMethod { get; private set; }

        public override void OnSendingRequest(HttpMessage message)
        {
            InvocationCount++;
            RequestMethod = message.Request.Method;
            throw new InvalidOperationException(ExceptionMessage);
        }
    }
}
