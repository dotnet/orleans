using Orleans.Serialization.Invocation;
using Orleans.Transactions;
using TestExtensions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Serialization;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class TransactionResponseTests
{
    [Fact]
    public void ToString_ReturnsInnerResponseTextWithoutThrowing()
    {
        var exception = new OrleansTransactionAbortedException("transaction-id", new InvalidOperationException("boom"));
        var response = TransactionResponse.Create(Response.FromException(exception), new TransactionInfo());

        var text = response.ToString();

        Assert.Contains(nameof(OrleansTransactionAbortedException), text);
        Assert.Contains("transaction-id", text);
        Assert.Throws<OrleansTransactionAbortedException>(() => _ = response.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransactionHostingRegistersProtocolStatusIdentity(bool silo)
    {
        var registrations = new ServiceCollection().AddSerializer();
        if (silo) registrations.UseTransactionsWithSilo();
        else registrations.UseTransactionsWithClient();
        using var services = registrations.BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();

        Assert.Same(typeof(TransactionalStatus),
            serializer.Deserialize<Type>(serializer.SerializeToArray<Type>(typeof(TransactionalStatus))));
        Assert.Equal(TransactionalStatus.Ok,
            serializer.Deserialize<TransactionalStatus>(serializer.SerializeToArray(TransactionalStatus.Ok)));
    }
}
