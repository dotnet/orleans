using System.Reflection;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Xunit;
using TransactionStorage = Orleans.Transactions.DynamoDB.DynamoDBStorage;

namespace AWSUtils.Tests.StorageTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Configuration")]
[TestCategory("BVT")]
public sealed class AwsCollectionCompatibilityTests
{
    [Theory]
    [InlineData(nameof(SqsOptions.ReceiveMessageSystemAttributes))]
    [InlineData(nameof(SqsOptions.ReceiveMessageAttributes))]
    public void SqsAttributeOptionsPreserveMutableListProperties(string propertyName)
    {
        var first = new SqsOptions();
        var second = new SqsOptions();
        var property = typeof(SqsOptions).GetProperty(propertyName)!;

        Assert.Equal(typeof(List<string>), property.PropertyType);
        var original = Assert.IsType<List<string>>(property.GetValue(first));
        Assert.Empty(original);
        Assert.NotSame(original, property.GetValue(second));
        var replacement = new List<string> { "first" };
        property.SetValue(first, replacement);
        replacement.AddRange(["second", "third"]);

        Assert.Same(replacement, property.GetValue(first));
        Assert.Equal(["first", "second", "third"], Assert.IsType<List<string>>(property.GetValue(first)));
        Assert.Empty(Assert.IsType<List<string>>(property.GetValue(second)));
    }

    [Fact]
    public void DynamoDbInitializationPreservesSdkListSignatureAndDefaults()
    {
        var method = typeof(TransactionStorage).GetMethod(nameof(TransactionStorage.InitializeTable),
            [typeof(string), typeof(List<KeySchemaElement>), typeof(List<AttributeDefinition>),
                typeof(List<GlobalSecondaryIndex>), typeof(string), typeof(CancellationToken)]);

        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method.ReturnType);
        var parameters = method.GetParameters();
        Assert.False(parameters[1].IsOptional);
        Assert.False(parameters[2].IsOptional);
        Assert.True(parameters[3].IsOptional);
        Assert.Null(parameters[3].DefaultValue);
        Assert.True(parameters[4].IsOptional);
        Assert.Null(parameters[4].DefaultValue);
        Assert.True(parameters[5].IsOptional);
        Assert.Equal(default(CancellationToken), parameters[5].DefaultValue ?? default(CancellationToken));
    }

    [Fact]
    public async Task DynamoDbTransactionPreservesSdkListAndReturnedTask()
    {
        using var client = new TransactionClient();
        var storage = new TransactionStorage(NullLogger<TransactionStorage>.Instance, service: "http://localhost");
        var clientField = typeof(TransactionStorage).GetField("_ddbClient", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.IsAssignableFrom<IDisposable>(clientField.GetValue(storage)).Dispose();
        clientField.SetValue(storage, client);
        var items = new List<TransactWriteItem>
        {
            new() { Delete = new Delete { TableName = "table", Key = new() { ["id"] = new AttributeValue { S = "first" } } } },
            new() { Delete = new Delete { TableName = "table", Key = new() { ["id"] = new AttributeValue { S = "second" } } } },
        };

        var operation = storage.WriteTxAsync(items);

        Assert.Same(client.Response, operation);
        Assert.Same(items, Assert.Single(client.Requests).TransactItems);
        Assert.Equal(["first", "second"], client.Requests[0].TransactItems.Select(item => item.Delete.Key["id"].S));
        await operation;
        var method = typeof(TransactionStorage).GetMethod(nameof(TransactionStorage.WriteTxAsync), [typeof(List<TransactWriteItem>)]);
        Assert.NotNull(method);
        Assert.Equal(typeof(Task), method.ReturnType);
    }

    private sealed class TransactionClient() : AmazonDynamoDBClient(
        new AnonymousAWSCredentials(), new AmazonDynamoDBConfig { ServiceURL = "http://localhost" })
    {
        public Task<TransactWriteItemsResponse> Response { get; } = Task.FromResult(new TransactWriteItemsResponse());
        public List<TransactWriteItemsRequest> Requests { get; } = [];

        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(
            TransactWriteItemsRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Response;
        }
    }
}
