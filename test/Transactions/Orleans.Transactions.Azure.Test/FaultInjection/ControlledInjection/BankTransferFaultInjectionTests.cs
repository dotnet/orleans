using AwesomeAssertions;
using Orleans.Transactions.AzureStorage.Tests;
using Orleans.Transactions.Tests;
using Orleans.Transactions.TestKit;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Azure.Tests.FaultInjection.ControlledInjection;

[TestCategory("AzureStorage"), TestCategory("Transactions"), TestCategory("Functional")]
public sealed class BankTransferFaultInjectionTests : IClassFixture<ControlledFaultInjectionTestFixture>
{
    private readonly IGrainFactory _grainFactory;
    private readonly ITestOutputHelper _output;

    public BankTransferFaultInjectionTests(ControlledFaultInjectionTestFixture fixture, ITestOutputHelper output)
    {
        fixture.EnsurePreconditionsMet();
        _grainFactory = fixture.GrainFactory;
        _output = output;
    }

    [Fact]
    public async Task StorageExceptionAfterCommitStore_DoesNotExposePartialBankTransfer()
    {
        BankTransferTrace.Clear();

        var from = _grainFactory.GetGrain<IBankTransferFaultInjectionAccountGrain>(Guid.NewGuid());
        var to = _grainFactory.GetGrain<IBankTransferFaultInjectionAccountGrain>(Guid.NewGuid());
        var teller = _grainFactory.GetGrain<IBankTransferFaultInjectionTellerGrain>(0);

        await from.SetBalance(0);
        await to.SetBalance(0);

        var commitFault = new BankTransferFault
        {
            Phase = TransactionFaultInjectPhase.BeforePrepareAndCommit,
            Type = FaultInjectionType.ExceptionAfterStore
        };

        // The deposit account is deliberately the transaction manager. Its store succeeds, then
        // faults before follow-up actions can confirm the withdrawal participant.
        var exception = await Assert.ThrowsAnyAsync<OrleansTransactionException>(
            () => teller.TransferReturnBalancesWithDepositAsManager(from, to, 1, commitFault));

        var committed = await teller.GetBalances(from, to);

        _output.WriteLine($"faultedTransferException={exception.GetType().Name}, committed={committed.From}+{committed.To}={committed.Total}");
        foreach (var traceEvent in BankTransferTrace.Snapshot().TakeLast(160))
        {
            _output.WriteLine($"{traceEvent.Timestamp:O} {traceEvent.TransactionId} {traceEvent.GrainId} {traceEvent.Stage} {traceEvent.Balance}");
        }

        committed.Total.Should().Be(0, "committed account balances should remain atomic after storage faults");
    }
}
