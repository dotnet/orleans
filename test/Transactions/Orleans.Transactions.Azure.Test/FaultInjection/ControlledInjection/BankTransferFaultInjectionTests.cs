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
    public async Task StorageExceptionAfterCommitStore_CommitsDurableFullBankTransfer()
    {
        var commitFault = new BankTransferFault
        {
            Phase = TransactionFaultInjectPhase.BeforePrepareAndCommit,
            Type = FaultInjectionType.ExceptionAfterStore
        };

        await RunFaultedDepositManagerTransfer(
            commitFault,
            100,
            0,
            expectedFrom: 99,
            expectedTo: 1,
            "the transaction manager committed its durable state before the storage exception was surfaced");
    }

    [Fact]
    public async Task GenericStorageExceptionAfterCommitStore_CommitsDurableFullBankTransfer()
    {
        var commitFault = new BankTransferFault
        {
            Phase = TransactionFaultInjectPhase.BeforePrepareAndCommit,
            Type = FaultInjectionType.GenericExceptionAfterStore
        };

        await RunFaultedDepositManagerTransfer(
            commitFault,
            100,
            0,
            expectedFrom: 99,
            expectedTo: 1,
            "a generic exception surfaced after the underlying storage write must recover the durable commit instead of partially aborting it");
    }

    [Fact]
    public async Task ExceptionAfterStorageWriteCompleted_CommitsDurableFullBankTransfer()
    {
        await RunFaultedDepositManagerTransfer(
            commitFault: null,
            100,
            0,
            expectedFrom: 99,
            expectedTo: 1,
            "the storage write returned successfully before a post-store queue step faulted, so recovery must reload and complete the durable commit",
            to => BankTransferDiagnosticFaults.ThrowOnStorageWriteCompleted(to));
    }

    private async Task RunFaultedDepositManagerTransfer(
        BankTransferFault commitFault,
        long initialFrom,
        long initialTo,
        long expectedFrom,
        long expectedTo,
        string because,
        Func<IBankTransferFaultInjectionAccountGrain, StorageWriteCompletedFaultScope> createDiagnosticFault = null)
    {
        BankTransferTrace.Clear();

        var from = _grainFactory.GetGrain<IBankTransferFaultInjectionAccountGrain>(Guid.NewGuid());
        var to = _grainFactory.GetGrain<IBankTransferFaultInjectionAccountGrain>(Guid.NewGuid());
        var teller = _grainFactory.GetGrain<IBankTransferFaultInjectionTellerGrain>(0);

        await from.SetBalance(initialFrom);
        await to.SetBalance(initialTo);

        StorageWriteCompletedFaultScope diagnosticFault = null;
        OrleansTransactionException exception;
        try
        {
            diagnosticFault = createDiagnosticFault?.Invoke(to);
            exception = await Assert.ThrowsAnyAsync<OrleansTransactionException>(
                () => teller.TransferReturnBalancesWithDepositAsManager(from, to, 1, commitFault));
        }
        finally
        {
            diagnosticFault?.Dispose();
        }

        if (diagnosticFault is not null)
        {
            diagnosticFault.FaultInjected.Should().BeTrue("the diagnostic fault should be injected exactly once for the target transaction manager");
            diagnosticFault.ObservedCount.Should().BeGreaterThan(0, "the diagnostic event should be emitted after the target storage write completes");
        }

        var committed = await teller.GetBalances(from, to);

        _output.WriteLine($"faultedTransferException={exception.GetType().Name}, committed={committed.From}+{committed.To}={committed.Total}");
        foreach (var traceEvent in BankTransferTrace.Snapshot().TakeLast(160))
        {
            _output.WriteLine($"{traceEvent.Timestamp:O} {traceEvent.TransactionId} {traceEvent.GrainId} {traceEvent.Stage} {traceEvent.Balance}");
        }

        committed.From.Should().Be(expectedFrom, because);
        committed.To.Should().Be(expectedTo, because);
        committed.Total.Should().Be(initialFrom + initialTo, "storage faults must not expose or durably persist a partial bank transfer");
    }
}
