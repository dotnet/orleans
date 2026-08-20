using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using TestExtensions;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public sealed class BankTransferFaultInjectionMemoryTests : IClassFixture<MemoryTransactionsFixture>
{
    private readonly IGrainFactory grainFactory;
    private readonly ITestOutputHelper output;

    public BankTransferFaultInjectionMemoryTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
    {
        grainFactory = fixture.GrainFactory;
        this.output = output;
    }

    [Fact]
    public async Task GenericExceptionAfterStorageWriteCompleted_CommitsDurableFullBankTransfer()
    {
        BankTransferTrace.Clear();

        var from = grainFactory.GetGrain<IBankTransferAccountGrain>(Guid.NewGuid());
        var to = grainFactory.GetGrain<IBankTransferAccountGrain>(Guid.NewGuid());
        var teller = grainFactory.GetGrain<IBankTransferTellerGrain>(0);

        await from.SetBalance(100);
        await to.SetBalance(0);

        var diagnosticFaults = new[]
        {
            BankTransferDiagnosticFaults.ThrowOnStorageWriteCompleted(from),
            BankTransferDiagnosticFaults.ThrowOnStorageWriteCompleted(to)
        };

        OrleansTransactionException exception;
        try
        {
            exception = await Assert.ThrowsAnyAsync<OrleansTransactionException>(
                () => teller.TransferReturnBalances(from, to, 1));
        }
        finally
        {
            foreach (var diagnosticFault in diagnosticFaults)
            {
                diagnosticFault.Dispose();
            }
        }

        diagnosticFaults.Should().Contain(
            diagnosticFault => diagnosticFault.FaultInjected,
            "the selected transaction manager should surface a single post-store fault");
        diagnosticFaults.Sum(diagnosticFault => diagnosticFault.ObservedCount).Should().BeGreaterThan(
            0,
            "the provider-neutral storage-write-completed diagnostic event should be observed");

        var committed = await teller.GetBalances(from, to);

        output.WriteLine($"faultedTransferException={exception.GetType().Name}, committed={committed.From}+{committed.To}={committed.Total}");
        foreach (var traceEvent in BankTransferTrace.Snapshot().TakeLast(160))
        {
            output.WriteLine($"{traceEvent.Timestamp:O} {traceEvent.TransactionId} {traceEvent.GrainId} {traceEvent.Stage} {traceEvent.Balance}");
        }

        committed.From.Should().Be(
            99,
            "the post-store exception is ambiguous and recovery must preserve the completed withdrawal");
        committed.To.Should().Be(
            1,
            "the post-store exception is ambiguous and recovery must preserve the completed deposit");
        committed.Total.Should().Be(100, "the durable result must remain a full committed bank transfer");
    }
}
