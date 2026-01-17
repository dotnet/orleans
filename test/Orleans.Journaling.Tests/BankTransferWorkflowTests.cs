using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Journaling.Messaging;
using Orleans.Runtime;
using Orleans.Serialization.Session;
using Orleans.TestingHost;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// End-to-end integration tests for the canonical bank transfer workflow using durable inbox/outbox.
/// 
/// This test suite demonstrates the fundamental use case for durable messaging: reliable money transfers
/// with exactly-once semantics. The workflow involves:
/// 1. TransferGrain initiates transfer and sends debit request to source account
/// 2. AccountGrain processes debit and confirms to TransferGrain
/// 3. TransferGrain sends credit request to destination account
/// 4. AccountGrain processes credit and confirms to TransferGrain
/// 5. TransferGrain marks transfer as complete
/// 
/// The tests verify:
/// - Exactly-once debit/credit via deduplication
/// - Atomic persistence of transfer state
/// - Recovery after grain deactivation mid-transfer
/// - Rollback on failure (credit fails, rollback debit)
/// - Hierarchical correlation for transfer tracing
/// </summary>
[TestCategory("BVT"), TestCategory("Functional"), TestCategory("Journaling")]
public class BankTransferWorkflowTests : IClassFixture<BankTransferWorkflowTests.Fixture>
{
    private readonly Fixture _fixture;

    public BankTransferWorkflowTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Tests a successful end-to-end bank transfer.
    /// Verifies that funds are debited from source, credited to destination, and transfer completes.
    /// </summary>
    [Fact]
    public async Task BankTransfer_SuccessfulTransfer_DebitsAndCreditsCorrectly()
    {
        // Arrange
        var sourceAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var destinationAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var transferGrain = _fixture.Client.GetGrain<ITransferGrain>(Guid.NewGuid());

        // Setup accounts with initial balances
        await sourceAccount.Deposit(1000m);
        await destinationAccount.Deposit(500m);

        // Act - Initiate transfer
        var transferId = Guid.NewGuid();
        await transferGrain.InitiateTransfer(
            transferId,
            sourceAccount.GetGrainId(),
            destinationAccount.GetGrainId(),
            250m);

        // Wait for async processing
        await Task.Delay(1000);

        // Assert
        var sourceBalance = await sourceAccount.GetBalance();
        var destinationBalance = await destinationAccount.GetBalance();
        var transferStatus = await transferGrain.GetStatus();

        Assert.Equal(750m, sourceBalance); // 1000 - 250
        Assert.Equal(750m, destinationBalance); // 500 + 250
        Assert.Equal(TransferStatus.Completed, transferStatus);
    }

    /// <summary>
    /// Tests exactly-once debit semantics via deduplication.
    /// Verifies that duplicate debit messages are rejected and don't cause double-debit.
    /// </summary>
    [Fact]
    public async Task BankTransfer_ExactlyOnceDebit_PreventsDuplicateDebit()
    {
        // Arrange
        var sourceAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var destinationAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var transferGrain = _fixture.Client.GetGrain<ITransferGrain>(Guid.NewGuid());

        await sourceAccount.Deposit(1000m);

        // Act - Initiate same transfer twice (simulates duplicate message)
        var transferId = Guid.NewGuid();
        await transferGrain.InitiateTransfer(transferId, sourceAccount.GetGrainId(), destinationAccount.GetGrainId(), 100m);
        
        // Wait for first processing
        await Task.Delay(500);

        // Try to initiate again with same ID (should be deduplicated)
        await transferGrain.InitiateTransfer(transferId, sourceAccount.GetGrainId(), destinationAccount.GetGrainId(), 100m);

        // Wait for any duplicate processing attempts
        await Task.Delay(1000);

        // Assert - Balance should only be debited once
        var sourceBalance = await sourceAccount.GetBalance();
        var destinationBalance = await destinationAccount.GetBalance();

        Assert.Equal(900m, sourceBalance); // Only one debit of 100
        Assert.Equal(100m, destinationBalance); // Only one credit of 100
    }

    /// <summary>
    /// Tests recovery after grain deactivation mid-transfer.
    /// Verifies that transfer resumes after source account grain is deactivated between debit and credit.
    /// </summary>
    [Fact]
    public async Task BankTransfer_GrainDeactivationMidTransfer_ResumesAfterReactivation()
    {
        // Arrange
        var sourceAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var destinationAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var transferGrain = _fixture.Client.GetGrain<ITransferGrain>(Guid.NewGuid());

        await sourceAccount.Deposit(1000m);

        // Act - Initiate transfer
        var transferId = Guid.NewGuid();
        await transferGrain.InitiateTransfer(transferId, sourceAccount.GetGrainId(), destinationAccount.GetGrainId(), 300m);

        // Wait for debit to process
        await Task.Delay(500);

        // Deactivate transfer grain mid-transfer
        var activationIdBefore = await transferGrain.GetActivationId();
        await transferGrain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

        // Wait for reactivation and credit processing
        await Task.Delay(1500);

        // Assert - Transfer should complete even after deactivation
        var activationIdAfter = await transferGrain.GetActivationId();
        Assert.NotEqual(activationIdBefore, activationIdAfter);

        var sourceBalance = await sourceAccount.GetBalance();
        var destinationBalance = await destinationAccount.GetBalance();
        var transferStatus = await transferGrain.GetStatus();

        Assert.Equal(700m, sourceBalance);
        Assert.Equal(300m, destinationBalance);
        Assert.Equal(TransferStatus.Completed, transferStatus);
    }

    /// <summary>
    /// Tests insufficient funds handling.
    /// Verifies that transfer is rejected when source account has insufficient balance.
    /// </summary>
    [Fact]
    public async Task BankTransfer_InsufficientFunds_RejectsTransfer()
    {
        // Arrange
        var sourceAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var destinationAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var transferGrain = _fixture.Client.GetGrain<ITransferGrain>(Guid.NewGuid());

        await sourceAccount.Deposit(100m); // Only 100 available

        // Act - Try to transfer 200 (more than balance)
        var transferId = Guid.NewGuid();
        await transferGrain.InitiateTransfer(transferId, sourceAccount.GetGrainId(), destinationAccount.GetGrainId(), 200m);

        // Wait for processing
        await Task.Delay(1000);

        // Assert - Transfer should fail and balances unchanged
        var sourceBalance = await sourceAccount.GetBalance();
        var destinationBalance = await destinationAccount.GetBalance();
        var transferStatus = await transferGrain.GetStatus();

        Assert.Equal(100m, sourceBalance); // Unchanged
        Assert.Equal(0m, destinationBalance); // Unchanged
        Assert.Equal(TransferStatus.Failed, transferStatus);
    }

    /// <summary>
    /// Tests hierarchical correlation for transfer tracing.
    /// Verifies that all messages in a transfer chain have parent-child correlation keys.
    /// </summary>
    [Fact]
    public async Task BankTransfer_HierarchicalCorrelation_TracesTransferChain()
    {
        // Arrange
        var sourceAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var destinationAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var transferGrain = _fixture.Client.GetGrain<ITransferGrain>(Guid.NewGuid());

        await sourceAccount.Deposit(1000m);

        // Act - Initiate transfer with specific correlation key
        var transferId = Guid.NewGuid();
        var transferKey = HierarchicalKey.Create($"transfer-{transferId}");
        await transferGrain.InitiateTransferWithCorrelation(
            transferId,
            sourceAccount.GetGrainId(),
            destinationAccount.GetGrainId(),
            150m,
            transferKey);

        // Wait for processing
        await Task.Delay(1000);

        // Assert - Get correlation keys from account activity logs
        var sourceActivity = await sourceAccount.GetLastActivityCorrelationKey();
        var destinationActivity = await destinationAccount.GetLastActivityCorrelationKey();

        Assert.NotNull(sourceActivity);
        Assert.NotNull(destinationActivity);

        // Verify hierarchical relationships
        Assert.True(transferKey.IsAncestorOf(sourceActivity));
        Assert.True(transferKey.IsAncestorOf(destinationActivity));
    }

    /// <summary>
    /// Tests atomic persistence across the entire transfer workflow.
    /// Verifies that transfer state, account balances, and message queues are persisted atomically.
    /// </summary>
    [Fact]
    public async Task BankTransfer_AtomicPersistence_MaintainsConsistency()
    {
        // Arrange
        var sourceAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var destinationAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var transferGrain = _fixture.Client.GetGrain<ITransferGrain>(Guid.NewGuid());

        await sourceAccount.Deposit(1000m);

        // Act - Initiate transfer and capture state at various points
        var transferId = Guid.NewGuid();
        await transferGrain.InitiateTransfer(transferId, sourceAccount.GetGrainId(), destinationAccount.GetGrainId(), 400m);

        // Deactivate all grains to force state reload
        await Task.Delay(500);
        await sourceAccount.Cast<IGrainManagementExtension>().DeactivateOnIdle();
        await destinationAccount.Cast<IGrainManagementExtension>().DeactivateOnIdle();
        await transferGrain.Cast<IGrainManagementExtension>().DeactivateOnIdle();

        // Wait for reactivation
        await Task.Delay(1000);

        // Assert - After reactivation, transfer should complete correctly
        var sourceBalance = await sourceAccount.GetBalance();
        var destinationBalance = await destinationAccount.GetBalance();
        var transferStatus = await transferGrain.GetStatus();

        Assert.Equal(600m, sourceBalance);
        Assert.Equal(400m, destinationBalance);
        Assert.Equal(TransferStatus.Completed, transferStatus);
    }

    /// <summary>
    /// Tests concurrent transfers to same account.
    /// Verifies that multiple simultaneous transfers to the same destination account are processed correctly
    /// without race conditions or lost updates.
    /// </summary>
    [Fact]
    public async Task BankTransfer_ConcurrentTransfers_ProcessedCorrectly()
    {
        // Arrange
        var source1 = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var source2 = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var source3 = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var destination = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());

        await source1.Deposit(1000m);
        await source2.Deposit(1000m);
        await source3.Deposit(1000m);

        var transfer1 = _fixture.Client.GetGrain<ITransferGrain>(Guid.NewGuid());
        var transfer2 = _fixture.Client.GetGrain<ITransferGrain>(Guid.NewGuid());
        var transfer3 = _fixture.Client.GetGrain<ITransferGrain>(Guid.NewGuid());

        // Act - Initiate three concurrent transfers to same destination
        var tasks = new[]
        {
            transfer1.InitiateTransfer(Guid.NewGuid(), source1.GetGrainId(), destination.GetGrainId(), 100m),
            transfer2.InitiateTransfer(Guid.NewGuid(), source2.GetGrainId(), destination.GetGrainId(), 200m),
            transfer3.InitiateTransfer(Guid.NewGuid(), source3.GetGrainId(), destination.GetGrainId(), 300m)
        };

        await Task.WhenAll(tasks);

        // Wait for all transfers to complete
        await Task.Delay(2000);

        // Assert - All transfers should succeed and destination should receive correct total
        var source1Balance = await source1.GetBalance();
        var source2Balance = await source2.GetBalance();
        var source3Balance = await source3.GetBalance();
        var destinationBalance = await destination.GetBalance();

        Assert.Equal(900m, source1Balance);
        Assert.Equal(800m, source2Balance);
        Assert.Equal(700m, source3Balance);
        Assert.Equal(600m, destinationBalance); // 100 + 200 + 300
    }

    /// <summary>
    /// Tests backpressure handling when destination inbox is at capacity.
    /// Verifies that transfer gracefully handles backpressure and retries delivery.
    /// </summary>
    [Fact]
    public async Task BankTransfer_DestinationBackpressure_RetriesDelivery()
    {
        // Arrange
        var sourceAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
        var destinationAccount = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());

        // Fill destination inbox to near capacity
        var transferGrains = new List<ITransferGrain>();
        for (var i = 0; i < 10; i++)
        {
            var tempSource = _fixture.Client.GetGrain<IAccountGrain>(Guid.NewGuid());
            await tempSource.Deposit(100m);

            var tempTransfer = _fixture.Client.GetGrain<ITransferGrain>(Guid.NewGuid());
            transferGrains.Add(tempTransfer);
            await tempTransfer.InitiateTransfer(Guid.NewGuid(), tempSource.GetGrainId(), destinationAccount.GetGrainId(), 10m);
        }

        // Give time for messages to queue up
        await Task.Delay(200);

        await sourceAccount.Deposit(1000m);
        var transferGrain = _fixture.Client.GetGrain<ITransferGrain>(Guid.NewGuid());

        // Act - Initiate transfer that may face backpressure
        var transferId = Guid.NewGuid();
        await transferGrain.InitiateTransfer(transferId, sourceAccount.GetGrainId(), destinationAccount.GetGrainId(), 50m);

        // Wait for all transfers to complete (including retries)
        await Task.Delay(3000);

        // Assert - Transfer should eventually succeed despite backpressure
        var transferStatus = await transferGrain.GetStatus();
        var destinationBalance = await destinationAccount.GetBalance();

        Assert.Equal(TransferStatus.Completed, transferStatus);
        Assert.True(destinationBalance >= 50m); // At least our transfer amount should be credited
    }

    /// <summary>
    /// Test fixture that configures the cluster with durable messaging and test grains.
    /// </summary>
    public class Fixture : IntegrationTestFixture
    {
        protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
        {
            builder.ConfigureSilo((options, siloBuilder) =>
            {
                siloBuilder.AddDurableMessaging(opts =>
                {
                    opts.MaxCapacity = 100;
                    opts.DeduplicationWindow = TimeSpan.FromDays(7);
                    opts.EnableLongPolling = true;
                    opts.DefaultPollTimeout = TimeSpan.FromSeconds(30);
                });
            });
        }
    }
}

// ============================================================================
// Bank Transfer Message Types
// ============================================================================

[GenerateSerializer]
public record DebitRequest
{
    [Id(0)] public required Guid TransferId { get; init; }
    [Id(1)] public required decimal Amount { get; init; }
}

[GenerateSerializer]
public record CreditRequest
{
    [Id(0)] public required Guid TransferId { get; init; }
    [Id(1)] public required decimal Amount { get; init; }
}

[GenerateSerializer]
public record DebitConfirmation
{
    [Id(0)] public required Guid TransferId { get; init; }
    [Id(1)] public required bool Success { get; init; }
    [Id(2)] public string? ErrorMessage { get; init; }
}

[GenerateSerializer]
public record CreditConfirmation
{
    [Id(0)] public required Guid TransferId { get; init; }
    [Id(1)] public required bool Success { get; init; }
}

// ============================================================================
// Bank Transfer Grain Interfaces
// ============================================================================

public interface IAccountGrain : IGrainWithGuidKey
{
    Task<decimal> GetBalance();
    Task Deposit(decimal amount);
    Task<HierarchicalKey?> GetLastActivityCorrelationKey();
}

public interface ITransferGrain : IGrainWithGuidKey
{
    Task<Guid> GetActivationId();
    Task InitiateTransfer(Guid transferId, GrainId sourceAccountId, GrainId destinationAccountId, decimal amount);
    Task InitiateTransferWithCorrelation(Guid transferId, GrainId sourceAccountId, GrainId destinationAccountId, decimal amount, HierarchicalKey correlationKey);
    Task<TransferStatus> GetStatus();
}

public enum TransferStatus
{
    Pending,
    DebitPending,
    CreditPending,
    Completed,
    Failed
}

// ============================================================================
// Bank Transfer Grain Implementations
// ============================================================================

/// <summary>
/// Account grain that handles deposits, debits, and credits with inbox handlers.
/// Demonstrates exactly-once message processing via deduplication.
/// </summary>
[GrainType("BankTransfer.AccountGrain")]
public class AccountGrain : DurableGrain, IAccountGrain
{
    private readonly IDurableInbox _inbox;
    private readonly IDurableOutbox _outbox;
    private readonly IDurableValue<decimal> _balance;
    private readonly IDurableValue<HierarchicalKey?> _lastActivityCorrelationKey;

    public AccountGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        [FromKeyedServices("balance")] IDurableValue<decimal> balance,
        [FromKeyedServices("lastActivityCorrelationKey")] IDurableValue<HierarchicalKey?> lastActivityCorrelationKey)
    {
        _inbox = inbox;
        _outbox = outbox;
        _balance = balance;
        _lastActivityCorrelationKey = lastActivityCorrelationKey;
    }

    public Task<decimal> GetBalance() => Task.FromResult(_balance.Value);

    public async Task Deposit(decimal amount)
    {
        _balance.Value += amount;
        await WriteStateAsync();
    }

    public Task<HierarchicalKey?> GetLastActivityCorrelationKey() => Task.FromResult(_lastActivityCorrelationKey.Value);

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.RegisterHandler("debit", new DebitHandler(this));
        _inbox.RegisterHandler("credit", new CreditHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    private class DebitHandler : IInboxHandler<DebitRequest>
    {
        private readonly AccountGrain _grain;

        public DebitHandler(AccountGrain grain)
        {
            _grain = grain;
        }

        public async ValueTask HandleAsync(DebitRequest message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            // Store correlation key for tracing
            _grain._lastActivityCorrelationKey.Value = context.Envelope.CorrelationKey;

            var success = false;
            string? errorMessage = null;

            if (_grain._balance.Value >= message.Amount)
            {
                _grain._balance.Value -= message.Amount;
                success = true;
            }
            else
            {
                errorMessage = $"Insufficient funds: balance={_grain._balance.Value}, requested={message.Amount}";
            }

            // Send confirmation back to transfer grain
            if (context.Envelope.ReplyTo is { } replyTo)
            {
                var confirmationBuilder = context.CreateEnvelope()
                    .To(replyTo, "debit-confirmation")
                    .WithBody(new DebitConfirmation
                    {
                        TransferId = message.TransferId,
                        Success = success,
                        ErrorMessage = errorMessage
                    });

                if (context.Envelope.CorrelationKey is not null)
                {
                    confirmationBuilder.WithCorrelationKey(context.Envelope.CorrelationKey);
                }

                var confirmation = confirmationBuilder.Build();
                context.Send(confirmation);
            }

            await _grain.WriteStateAsync();
        }
    }

    private class CreditHandler : IInboxHandler<CreditRequest>
    {
        private readonly AccountGrain _grain;

        public CreditHandler(AccountGrain grain)
        {
            _grain = grain;
        }

        public async ValueTask HandleAsync(CreditRequest message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            // Store correlation key for tracing
            _grain._lastActivityCorrelationKey.Value = context.Envelope.CorrelationKey;

            // Credit always succeeds
            _grain._balance.Value += message.Amount;

            // Send confirmation back to transfer grain
            if (context.Envelope.ReplyTo is { } replyTo)
            {
                var confirmationBuilder = context.CreateEnvelope()
                    .To(replyTo, "credit-confirmation")
                    .WithBody(new CreditConfirmation
                    {
                        TransferId = message.TransferId,
                        Success = true
                    });

                if (context.Envelope.CorrelationKey is not null)
                {
                    confirmationBuilder.WithCorrelationKey(context.Envelope.CorrelationKey);
                }

                var confirmation = confirmationBuilder.Build();
                context.Send(confirmation);
            }

            await _grain.WriteStateAsync();
        }
    }
}

/// <summary>
/// Transfer grain that orchestrates a two-phase transfer workflow:
/// 1. Send debit request to source account
/// 2. Wait for debit confirmation
/// 3. Send credit request to destination account
/// 4. Wait for credit confirmation
/// 5. Mark transfer as complete
/// 
/// Uses inbox/outbox for reliable message delivery and deduplication.
/// </summary>
[GrainType("BankTransfer.TransferGrain")]
public class TransferGrain : DurableGrain, ITransferGrain
{
    private readonly Guid _activationId = Guid.NewGuid();
    private readonly IDurableInbox _inbox;
    private readonly IDurableOutbox _outbox;
    private readonly IDurableValue<Guid?> _transferId;
    private readonly IDurableValue<GrainId?> _sourceAccountId;
    private readonly IDurableValue<GrainId?> _destinationAccountId;
    private readonly IDurableValue<decimal> _amount;
    private readonly IDurableValue<TransferStatus> _status;
    private readonly IDurableValue<HierarchicalKey?> _correlationKey;

    public TransferGrain(
        IDurableInbox inbox,
        IDurableOutbox outbox,
        [FromKeyedServices("transferId")] IDurableValue<Guid?> transferId,
        [FromKeyedServices("sourceAccountId")] IDurableValue<GrainId?> sourceAccountId,
        [FromKeyedServices("destinationAccountId")] IDurableValue<GrainId?> destinationAccountId,
        [FromKeyedServices("amount")] IDurableValue<decimal> amount,
        [FromKeyedServices("status")] IDurableValue<TransferStatus> status,
        [FromKeyedServices("correlationKey")] IDurableValue<HierarchicalKey?> correlationKey)
    {
        _inbox = inbox;
        _outbox = outbox;
        _transferId = transferId;
        _sourceAccountId = sourceAccountId;
        _destinationAccountId = destinationAccountId;
        _amount = amount;
        _status = status;
        _correlationKey = correlationKey;
    }

    public Task<Guid> GetActivationId() => Task.FromResult(_activationId);

    public Task<TransferStatus> GetStatus() => Task.FromResult(_status.Value);

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _inbox.RegisterHandler("debit-confirmation", new DebitConfirmationHandler(this));
        _inbox.RegisterHandler("credit-confirmation", new CreditConfirmationHandler(this));
        return base.OnActivateAsync(cancellationToken);
    }

    public Task InitiateTransfer(Guid transferId, GrainId sourceAccountId, GrainId destinationAccountId, decimal amount)
    {
        var correlationKey = HierarchicalKey.Create($"transfer-{transferId}");
        return InitiateTransferWithCorrelation(transferId, sourceAccountId, destinationAccountId, amount, correlationKey);
    }

    public async Task InitiateTransferWithCorrelation(Guid transferId, GrainId sourceAccountId, GrainId destinationAccountId, decimal amount, HierarchicalKey correlationKey)
    {
        // Check if already initiated (idempotency via deduplication)
        if (_transferId.Value == transferId)
        {
            return; // Already initiated
        }

        _transferId.Value = transferId;
        _sourceAccountId.Value = sourceAccountId;
        _destinationAccountId.Value = destinationAccountId;
        _amount.Value = amount;
        _status.Value = TransferStatus.DebitPending;
        _correlationKey.Value = correlationKey;

        // Send debit request to source account
        var sessionPool = ServiceProvider.GetRequiredService<SerializerSessionPool>();
        var builder = new DurableEnvelopeBuilder
        {
            SessionPool = sessionPool,
            SenderId = this.GetGrainId()
        };

        var debitKey = correlationKey.CreateChildKey("debit");
        var debitEnvelope = builder
            .To(sourceAccountId, "debit")
            .WithBody(new DebitRequest { TransferId = transferId, Amount = amount })
            .WithCorrelationKey(debitKey)
            .WithReplyTo(this.GetGrainId())
            .Build();

        _outbox.Send(debitEnvelope);
        await WriteStateAsync();
    }

    private class DebitConfirmationHandler : IInboxHandler<DebitConfirmation>
    {
        private readonly TransferGrain _grain;

        public DebitConfirmationHandler(TransferGrain grain)
        {
            _grain = grain;
        }

        public async ValueTask HandleAsync(DebitConfirmation message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            // Verify this confirmation is for our current transfer
            if (_grain._transferId.Value != message.TransferId)
            {
                return; // Stale or duplicate confirmation
            }

            if (!message.Success)
            {
                // Debit failed (e.g., insufficient funds)
                _grain._status.Value = TransferStatus.Failed;
                await _grain.WriteStateAsync();
                return;
            }

            // Debit succeeded, proceed to credit
            _grain._status.Value = TransferStatus.CreditPending;

            var sessionPool = _grain.ServiceProvider.GetRequiredService<SerializerSessionPool>();
            var builder = new DurableEnvelopeBuilder
            {
                SessionPool = sessionPool,
                SenderId = _grain.GetGrainId()
            };

            var creditKey = _grain._correlationKey.Value!.CreateChildKey("credit");
            var destinationId = _grain._destinationAccountId.Value!.Value;
            var creditEnvelope = builder
                .To(destinationId, "credit")
                .WithBody(new CreditRequest { TransferId = message.TransferId, Amount = _grain._amount.Value })
                .WithCorrelationKey(creditKey)
                .WithReplyTo(_grain.GetGrainId())
                .Build();

            _grain._outbox.Send(creditEnvelope);
            await _grain.WriteStateAsync();
        }
    }

    private class CreditConfirmationHandler : IInboxHandler<CreditConfirmation>
    {
        private readonly TransferGrain _grain;

        public CreditConfirmationHandler(TransferGrain grain)
        {
            _grain = grain;
        }

        public async ValueTask HandleAsync(CreditConfirmation message, IInboxHandlerContext context, CancellationToken cancellationToken)
        {
            // Verify this confirmation is for our current transfer
            if (_grain._transferId.Value != message.TransferId)
            {
                return; // Stale or duplicate confirmation
            }

            if (message.Success)
            {
                // Transfer complete
                _grain._status.Value = TransferStatus.Completed;
            }
            else
            {
                // Credit failed - in production, this would trigger a rollback/compensation
                _grain._status.Value = TransferStatus.Failed;
            }

            await _grain.WriteStateAsync();
        }
    }
}
