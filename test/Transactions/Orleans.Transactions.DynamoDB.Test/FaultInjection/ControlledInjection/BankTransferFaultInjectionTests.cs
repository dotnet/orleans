using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Orleans.Transactions.TestKit;
using Orleans.Transactions.Tests;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.DynamoDB.Tests.FaultInjection.ControlledInjection;

[TestSuite("Functional")]
[TestProvider("DynamoDB")]
[TestArea("Transactions")]
[TestCategory("DynamoDB"), TestCategory("Transactions"), TestCategory("Functional")]
public sealed class BankTransferFaultInjectionTests : IClassFixture<ControlledFaultInjectionTestFixture>
{
    private readonly IGrainFactory grainFactory;
    private readonly ITestOutputHelper output;

    public BankTransferFaultInjectionTests(ControlledFaultInjectionTestFixture fixture, ITestOutputHelper output)
    {
        fixture.EnsurePreconditionsMet();
        grainFactory = fixture.GrainFactory;
        this.output = output;
    }

    [SkippableFact]
    public async Task StorageExceptionAfterCommitStore_CommitsDurableFullBankTransfer()
    {
        var commitFault = new BankTransferFault
        {
            Phase = TransactionFaultInjectPhase.BeforePrepareAndCommit,
            Type = FaultInjectionType.ExceptionAfterStore
        };

        await RunFaultedTransfer(
            commitFault,
            expectedFrom: 99,
            expectedTo: 1,
            "the transaction manager committed its durable state before the storage exception was surfaced");
    }

    [SkippableFact]
    public async Task GenericStorageExceptionAfterCommitStore_CommitsDurableFullBankTransfer()
    {
        var commitFault = new BankTransferFault
        {
            Phase = TransactionFaultInjectPhase.BeforePrepareAndCommit,
            Type = FaultInjectionType.GenericExceptionAfterStore
        };

        await RunFaultedTransfer(
            commitFault,
            expectedFrom: 99,
            expectedTo: 1,
            "a generic exception surfaced after the underlying storage write and recovery must preserve the committed transfer");
    }

    private async Task RunFaultedTransfer(BankTransferFault commitFault, long expectedFrom, long expectedTo, string because)
    {
        BankTransferTrace.Clear();

        var from = grainFactory.GetGrain<IBankTransferFaultInjectionAccountGrain>(Guid.NewGuid());
        var to = grainFactory.GetGrain<IBankTransferFaultInjectionAccountGrain>(Guid.NewGuid());
        var teller = grainFactory.GetGrain<IBankTransferFaultInjectionTellerGrain>(0);

        await from.SetBalance(100);
        await to.SetBalance(0);

        var exception = await Assert.ThrowsAnyAsync<OrleansTransactionException>(
            () => teller.TransferReturnBalancesWithDepositAsManager(from, to, 1, commitFault));

        var committed = await teller.GetBalances(from, to);

        output.WriteLine($"faultedTransferException={exception.GetType().Name}, committed={committed.From}+{committed.To}={committed.Total}");
        foreach (var traceEvent in BankTransferTrace.Snapshot().TakeLast(160))
        {
            output.WriteLine($"{traceEvent.Timestamp:O} {traceEvent.TransactionId} {traceEvent.GrainId} {traceEvent.Stage} {traceEvent.Balance}");
        }

        committed.From.Should().Be(expectedFrom, because);
        committed.To.Should().Be(expectedTo, because);
        committed.Total.Should().Be(100, "ambiguous storage faults must not durably persist a partial bank transfer");
    }
}
