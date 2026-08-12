using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Azure.Storage.Blobs;
using Orleans;
using Orleans.EventSourcing;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Hosting;
using Orleans.Providers;
using Orleans.Serialization;

namespace Documentation.EventSourcing.Configuration
{
    internal static class Hosting
    {
        internal static void Configure(HostApplicationBuilder builder)
        {
            // <register_log_consistency>
builder.UseOrleans(siloBuilder =>
{
    siloBuilder
        .AddAzureBlobGrainStorage("eventStore", options =>
        {
            var connectionString =
                builder.Configuration.GetConnectionString("eventStore")
                ?? throw new InvalidOperationException(
                    "The eventStore connection string isn't configured.");
            options.BlobServiceClient =
                new BlobServiceClient(connectionString);
        })
        .AddStateStorageBasedLogConsistencyProvider("snapshots")
        .AddLogStorageBasedLogConsistencyProvider("shortLogs");
});
            // </register_log_consistency>
        }
    }

    public sealed class AccountState;

    public abstract record AccountEvent;

    public interface IAccountGrain : IGrainWithStringKey;

    // <select_log_consistency_provider>
[LogConsistencyProvider(ProviderName = "snapshots")]
[StorageProvider(ProviderName = "eventStore")]
public sealed class AccountGrain
    : JournaledGrain<AccountState, AccountEvent>, IAccountGrain
{
}
    // </select_log_consistency_provider>
}

namespace Documentation.EventSourcing.Configuration.Custom
{
    public sealed class AccountState;

    public abstract record AccountEvent;

    public interface IAccountGrain : IGrainWithStringKey;

    // <custom_storage_grain>
[LogConsistencyProvider(ProviderName = "custom")]
public sealed class AccountGrain
    : JournaledGrain<AccountState, AccountEvent>,
      IAccountGrain,
      ICustomStorageInterface<AccountState, AccountEvent>
{
    // <custom_storage_operations>
    public Task<KeyValuePair<int, AccountState>> ReadStateFromStorage() =>
        throw new NotImplementedException();

    public Task<bool> ApplyUpdatesToStorage(
        IReadOnlyList<AccountEvent> updates,
        int expectedVersion) =>
        throw new NotImplementedException();

    public Task ClearStoredState() =>
        throw new NotImplementedException();
    // </custom_storage_operations>
}
    // </custom_storage_grain>
}

namespace Documentation.EventSourcing.Basics
{
    public interface IAccountGrain : IGrainWithStringKey;

    public abstract record AccountEvent;

    // <journaled_grain>
public sealed class AccountGrain
    : JournaledGrain<AccountState, AccountEvent>, IAccountGrain
{
}
    // </journaled_grain>

    // <event_sourced_state>
[GenerateSerializer]
public sealed class AccountState
{
    [Id(0)]
    public decimal Balance { get; private set; }

    public void Apply(Deposited deposited) =>
        Balance += deposited.Amount;

    public void Apply(Withdrawn withdrawn) =>
        Balance -= withdrawn.Amount;
}
    // </event_sourced_state>

    [GenerateSerializer]
    public sealed record Deposited([property: Id(0)] decimal Amount) : AccountEvent;

    [GenerateSerializer]
    public sealed record Withdrawn([property: Id(0)] decimal Amount) : AccountEvent;

    internal sealed class Examples : JournaledGrain<AccountState, AccountEvent>
    {
        internal async Task RaiseAndConfirm(decimal amount)
        {
            // <raise_and_confirm>
RaiseEvent(new Deposited(amount));
await ConfirmEvents();
            // </raise_and_confirm>
        }

        internal async Task RaiseManyAndConfirm(IReadOnlyList<AccountEvent> events)
        {
            // <raise_many_and_confirm>
RaiseEvents(events);
await ConfirmEvents();
            // </raise_many_and_confirm>
        }

        internal async Task<bool> Withdraw(decimal amount)
        {
            // <raise_conditional_event>
if (!await RaiseConditionalEvent(new Withdrawn(amount)))
{
    return false;
}
            // </raise_conditional_event>

            return true;
        }

        internal async Task Refresh()
        {
            // <refresh_now>
await RefreshNow();
            // </refresh_now>
        }
    }
}

namespace Documentation.EventSourcing.Confirmation
{
    public sealed class AccountState;

    public abstract record AccountEvent;

    public sealed record Deposited(decimal Amount) : AccountEvent;

    internal sealed class AccountGrain : JournaledGrain<AccountState, AccountEvent>
    {
        internal async Task Deposit(decimal amount)
        {
            // <immediate_confirmation>
RaiseEvent(new Deposited(amount));
await ConfirmEvents();
            // </immediate_confirmation>
        }
    }
}

namespace Documentation.EventSourcing.Diagnostics
{
    public sealed class AccountState;

    public abstract record AccountEvent;

    internal sealed class AccountGrain : JournaledGrain<AccountState, AccountEvent>
    {
        // <connection_issue_callbacks>
protected override void OnConnectionIssue(ConnectionIssue issue)
{
    // Record the issue category, retry count, and exception.
}

protected override void OnConnectionIssueResolved(ConnectionIssue issue)
{
    // Clear or resolve the corresponding health signal.
}
        // </connection_issue_callbacks>
    }
}

namespace Documentation.EventSourcing.Notifications
{
    public sealed class AccountState;

    public abstract record AccountEvent;

    internal sealed class AccountGrain : JournaledGrain<AccountState, AccountEvent>
    {
        // <confirmed_state_changed>
protected override void OnStateChanged()
{
    // Inspect State and Version.
}
        // </confirmed_state_changed>

        // <tentative_state_changed>
protected override void OnTentativeStateChanged()
{
    // Inspect TentativeState and UnconfirmedEvents.
}
        // </tentative_state_changed>
    }
}

namespace Documentation.EventSourcing.ReplicatedInstances
{
    public sealed class AccountState;

    public abstract record AccountEvent;

    public sealed record Withdrawn(decimal Amount) : AccountEvent;

    internal sealed class AccountGrain : JournaledGrain<AccountState, AccountEvent>
    {
        internal async Task<bool> Withdraw(decimal amount)
        {
            // <conditional_update>
var accepted = await RaiseConditionalEvent(new Withdrawn(amount));
            // </conditional_update>

            return accepted;
        }

        internal async Task Refresh()
        {
            // <refresh_replicated_instance>
await RefreshNow();
            // </refresh_replicated_instance>
        }
    }
}
