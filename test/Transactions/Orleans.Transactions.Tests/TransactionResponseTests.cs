using Orleans.Serialization.Invocation;
using Orleans.Transactions;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

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
}
