using System.Collections.Concurrent;
using Orleans.Concurrency;
using Orleans.Transactions.Abstractions;
using Orleans.Transactions.TestKit;

namespace Orleans.Transactions.Tests;

public interface IBankTransferAccountGrain : IGrainWithGuidKey
{
    [Transaction(TransactionOption.CreateOrJoin)]
    Task SetBalance(long balance);

    [Transaction(TransactionOption.CreateOrJoin)]
    Task<long> GetBalance();

    [Transaction(TransactionOption.Join)]
    Task<long> WithdrawReturnBalance(long units);

    [Transaction(TransactionOption.Join)]
    Task<long> DepositReturnBalance(long units);
}

public interface IBankTransferTellerGrain : IGrainWithIntegerKey
{
    [Transaction(TransactionOption.Create)]
    [ReadOnly]
    Task<BankTransferResult> GetBalances(IBankTransferAccountGrain from, IBankTransferAccountGrain to);

    [Transaction(TransactionOption.Create)]
    Task<BankTransferResult> TransferReturnBalances(
        IBankTransferAccountGrain from,
        IBankTransferAccountGrain to,
        long units);
}

[GenerateSerializer]
public sealed class BankTransferResult
{
    [Id(0)]
    public long From { get; set; }

    [Id(1)]
    public long To { get; set; }

    [Id(2)]
    public string TransactionId { get; set; } = string.Empty;

    public long Total => From + To;
}

[GenerateSerializer]
public sealed class BankTransferAccountState
{
    [Id(0)]
    public long Balance { get; set; }
}

public sealed record BankTransferTraceEvent(
    DateTime Timestamp,
    string TransactionId,
    string GrainId,
    string Stage,
    long? Balance);

public static class BankTransferTrace
{
    private static readonly ConcurrentQueue<BankTransferTraceEvent> Events = new();

    public static void Clear()
    {
        Events.Clear();
    }

    public static IReadOnlyList<BankTransferTraceEvent> Snapshot()
    {
        return [.. Events];
    }

    internal static void Record(IAddressable grain, string stage, long? balance = null)
    {
        Events.Enqueue(new BankTransferTraceEvent(
            DateTime.UtcNow,
            TransactionContext.GetTransactionInfo()?.Id ?? string.Empty,
            grain.GetGrainId().ToString(),
            stage,
            balance));
    }
}

public sealed class BankTransferAccountGrain(
    [TransactionalState("balance", TransactionTestConstants.TransactionStore)]
    ITransactionalState<BankTransferAccountState> balance) : Grain, IBankTransferAccountGrain
{
    public Task SetBalance(long value)
    {
        return balance.PerformUpdate(state =>
        {
            state.Balance = value;
            BankTransferTrace.Record(this, "account-set", state.Balance);
        });
    }

    public Task<long> GetBalance()
    {
        return balance.PerformRead(state =>
        {
            BankTransferTrace.Record(this, "account-read", state.Balance);
            return state.Balance;
        });
    }

    public Task<long> WithdrawReturnBalance(long units)
    {
        return balance.PerformUpdate(state =>
        {
            state.Balance -= units;
            BankTransferTrace.Record(this, "withdraw-after-update", state.Balance);
            return state.Balance;
        });
    }

    public Task<long> DepositReturnBalance(long units)
    {
        return balance.PerformUpdate(state =>
        {
            state.Balance += units;
            BankTransferTrace.Record(this, "deposit-after-update", state.Balance);
            return state.Balance;
        });
    }
}

[GenerateSerializer]
public sealed class BankTransferFault
{
    [Id(0)]
    public TransactionFaultInjectPhase Phase { get; set; }

    [Id(1)]
    public FaultInjectionType Type { get; set; }

    public FaultInjectionControl ToControl() => new() { FaultInjectionPhase = Phase, FaultInjectionType = Type };
}

public interface IBankTransferFaultInjectionAccountGrain : IGrainWithGuidKey
{
    [Transaction(TransactionOption.CreateOrJoin)]
    Task SetBalance(long balance);

    [Transaction(TransactionOption.CreateOrJoin)]
    Task<long> GetBalance();

    [Transaction(TransactionOption.Join)]
    Task<long> WithdrawReturnBalance(long units, BankTransferFault fault = null);

    [Transaction(TransactionOption.Join)]
    Task<long> DepositReturnBalance(long units, BankTransferFault fault = null);
}

public interface IBankTransferFaultInjectionTellerGrain : IGrainWithIntegerKey
{
    [Transaction(TransactionOption.Create)]
    [ReadOnly]
    Task<BankTransferResult> GetBalances(
        IBankTransferFaultInjectionAccountGrain from,
        IBankTransferFaultInjectionAccountGrain to);

    [Transaction(TransactionOption.Create)]
    Task<BankTransferResult> TransferReturnBalances(
        IBankTransferFaultInjectionAccountGrain from,
        IBankTransferFaultInjectionAccountGrain to,
        long units,
        BankTransferFault fromFault = null,
        BankTransferFault toFault = null);

    [Transaction(TransactionOption.Create)]
    Task<BankTransferResult> TransferReturnBalancesWithDepositAsManager(
        IBankTransferFaultInjectionAccountGrain from,
        IBankTransferFaultInjectionAccountGrain to,
        long units,
        BankTransferFault toFault = null);
}

public sealed class BankTransferFaultInjectionAccountGrain(
    [FaultInjectionTransactionalState("balance", TransactionTestConstants.TransactionStore)]
    IFaultInjectionTransactionalState<BankTransferAccountState> balance) : Grain, IBankTransferFaultInjectionAccountGrain
{
    public Task SetBalance(long value)
    {
        ClearFault();
        return balance.PerformUpdate(state =>
        {
            state.Balance = value;
            BankTransferTrace.Record(this, "fault-account-set", state.Balance);
        });
    }

    public Task<long> GetBalance()
    {
        ClearFault();
        return balance.PerformRead(state =>
        {
            BankTransferTrace.Record(this, "fault-account-read", state.Balance);
            return state.Balance;
        });
    }

    public Task<long> WithdrawReturnBalance(long units, BankTransferFault fault = null)
    {
        ApplyFault(fault);
        return balance.PerformUpdate(state =>
        {
            state.Balance -= units;
            BankTransferTrace.Record(this, "fault-withdraw-after-update", state.Balance);
            return state.Balance;
        });
    }

    public Task<long> DepositReturnBalance(long units, BankTransferFault fault = null)
    {
        ApplyFault(fault);
        return balance.PerformUpdate(state =>
        {
            state.Balance += units;
            BankTransferTrace.Record(this, "fault-deposit-after-update", state.Balance);
            return state.Balance;
        });
    }

    private void ClearFault() => balance.FaultInjectionControl.Reset();

    private void ApplyFault(BankTransferFault fault)
    {
        ClearFault();
        if (fault is null)
        {
            return;
        }

        balance.FaultInjectionControl.FaultInjectionPhase = fault.Phase;
        balance.FaultInjectionControl.FaultInjectionType = fault.Type;
    }
}

[StatelessWorker]
public sealed class BankTransferTellerGrain : Grain, IBankTransferTellerGrain
{
    public async Task<BankTransferResult> GetBalances(IBankTransferAccountGrain from, IBankTransferAccountGrain to)
    {
        BankTransferTrace.Record(this, "teller-read-start");
        var balances = await Task.WhenAll(from.GetBalance(), to.GetBalance());
        var result = new BankTransferResult { From = balances[0], To = balances[1], TransactionId = TransactionContext.CurrentTransactionId };
        BankTransferTrace.Record(this, "teller-read-complete", result.Total);
        return result;
    }

    public async Task<BankTransferResult> TransferReturnBalances(
        IBankTransferAccountGrain from,
        IBankTransferAccountGrain to,
        long units)
    {
        BankTransferTrace.Record(this, "teller-transfer-start");
        var balances = await Task.WhenAll(
            from.WithdrawReturnBalance(units),
            to.DepositReturnBalance(units));

        var result = new BankTransferResult { From = balances[0], To = balances[1], TransactionId = TransactionContext.CurrentTransactionId };
        BankTransferTrace.Record(this, "teller-transfer-complete", result.Total);
        return result;
    }
}

[StatelessWorker]
public sealed class BankTransferFaultInjectionTellerGrain : Grain, IBankTransferFaultInjectionTellerGrain
{
    public async Task<BankTransferResult> GetBalances(
        IBankTransferFaultInjectionAccountGrain from,
        IBankTransferFaultInjectionAccountGrain to)
    {
        BankTransferTrace.Record(this, "fault-teller-read-start");
        var balances = await Task.WhenAll(from.GetBalance(), to.GetBalance());
        var result = new BankTransferResult { From = balances[0], To = balances[1], TransactionId = TransactionContext.CurrentTransactionId };
        BankTransferTrace.Record(this, "fault-teller-read-complete", result.Total);
        return result;
    }

    public async Task<BankTransferResult> TransferReturnBalances(
        IBankTransferFaultInjectionAccountGrain from,
        IBankTransferFaultInjectionAccountGrain to,
        long units,
        BankTransferFault fromFault = null,
        BankTransferFault toFault = null)
    {
        BankTransferTrace.Record(this, "fault-teller-transfer-start");
        var balances = await Task.WhenAll(
            from.WithdrawReturnBalance(units, fromFault),
            to.DepositReturnBalance(units, toFault));

        var result = new BankTransferResult { From = balances[0], To = balances[1], TransactionId = TransactionContext.CurrentTransactionId };
        BankTransferTrace.Record(this, "fault-teller-transfer-complete", result.Total);
        return result;
    }

    public async Task<BankTransferResult> TransferReturnBalancesWithDepositAsManager(
        IBankTransferFaultInjectionAccountGrain from,
        IBankTransferFaultInjectionAccountGrain to,
        long units,
        BankTransferFault toFault = null)
    {
        BankTransferTrace.Record(this, "fault-teller-deposit-manager-transfer-start");
        var toBalance = await to.DepositReturnBalance(units, toFault);
        var fromBalance = await from.WithdrawReturnBalance(units);

        var result = new BankTransferResult { From = fromBalance, To = toBalance, TransactionId = TransactionContext.CurrentTransactionId };
        BankTransferTrace.Record(this, "fault-teller-deposit-manager-transfer-complete", result.Total);
        return result;
    }
}
