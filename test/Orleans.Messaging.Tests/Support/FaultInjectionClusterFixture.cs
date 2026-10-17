using Microsoft.Extensions.DependencyInjection;
using Orleans.DurableJobs;
using Orleans.Messaging.Configuration;
using Orleans.Runtime;
using Xunit;

namespace Orleans.Messaging.Tests.Support;

/// <summary>
/// A messaging cluster whose delivery calls, journal writes, Durable Jobs scheduling, and Durable Jobs clock are all
/// under test control, for deterministic fault injection at every boundary of the delivery chain.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Deliveries"/> injects faults into the outbox's call to the receiver; <see cref="MessagingClusterFixture.Storage"/>
/// fails or holds journal writes before or after they become durable; <see cref="MessagingClusterFixture.JobManagerProbe"/>
/// fails or holds wake-up scheduling. <see cref="JobClock"/> drives the outbox's retry backoff and expiry and scheduled
/// delivery times, and moves only when a test advances it.
/// </para>
/// <para>
/// Durable Jobs fires a wake-up which is due when it is scheduled at once, but a wake-up which is due later waits in real
/// time; <see cref="RetryInterval"/> is long enough that such wake-ups never fire during a test. After advancing
/// <see cref="JobClock"/>, tests run the pump explicitly through <see cref="AdvanceAndRunPumpAsync"/>.
/// </para>
/// <para>
/// <see cref="Configuration.DurableMessagingOptions.InboxRetryTimeout"/> stays zero, so a handler canceled by
/// deactivation which were wrongly counted as a failure would dead-letter at once.
/// </para>
/// </remarks>
public class FaultInjectionClusterFixture() : MessagingClusterFixture(1, receiverOnly: false)
{
    /// <summary>The first outbox retry backoff, which doubles with each further attempt.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(10);

    /// <summary>How long the outbox retries a message before it dead-letters the message.</summary>
    public static readonly TimeSpan OutboxRetryTimeout = TimeSpan.FromHours(1);

    /// <summary>Gets the Durable Jobs clock, which the outbox and scheduled deliveries use.</summary>
    public ManualJobClock JobClock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>Gets the fault injector for the outbox's calls to receivers.</summary>
    public DeliveryFaultInjector Deliveries { get; } = new();

    /// <summary>Gets the probe for <see cref="Functional.SourceSinkGrain"/> activations.</summary>
    public Functional.SourceSinkProbe SourceSinkProbe { get; } = new();

    /// <summary>Advances <see cref="JobClock"/> by <paramref name="delta"/>, then runs one pump pass for <paramref name="grain"/>.</summary>
    public async Task<DurableJobRunResult> AdvanceAndRunPumpAsync(IAddressable grain, TimeSpan delta)
    {
        JobClock.Advance(delta);
        return await RunPumpAsync(grain);
    }

    /// <summary>Waits until the outbox of <paramref name="sender"/> has recorded <paramref name="attempts"/> failed attempts for <paramref name="messageId"/>.</summary>
    internal Task WaitForFailedAttemptsAsync(IAddressable sender, MessageId messageId, int attempts) =>
        WaitUntilAsync(sender, () =>
            GetOutboxState(sender) is var state
            && state.AttemptKeys.Contains(messageId)
            && state.Attempt(messageId).AttemptCount >= attempts);

    /// <summary>Gets test access to the outbox state of the active <paramref name="grain"/>.</summary>
    internal OutboxStateTestAccess GetOutboxState(IAddressable grain) =>
        new(GetGrainContext(grain).ActivationServices.GetRequiredService<IOutbox>());

    /// <summary>
    /// Deactivates <paramref name="grain"/> as a messaging failure does, which cancels its in-flight deliveries and
    /// handlers, and waits until the activation is gone.
    /// </summary>
    public async Task CrashAsync(IAddressable grain)
    {
        var context = GetGrainContext(grain);
        await OnGrainAsync(grain, () => context.Deactivate(
            new DeactivationReason(DeactivationReasonCode.ApplicationError, "Injected activation crash.")));
        await context.Deactivated.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddKeyedSingleton<TimeProvider>(DurableJobTimeProviderNames.DurableJobs, JobClock);
        services.AddSingleton(SourceSinkProbe);
    }

    protected override void ConfigureOptions(DurableMessagingOptions options)
    {
        base.ConfigureOptions(options);
        options.MaxCapacity = 8;
        options.DeduplicationWindow = TimeSpan.FromDays(1);
        options.OutboxRetryTimeout = OutboxRetryTimeout;
        options.RetryInitialInterval = RetryInterval;
        options.DeliveryResiliencePipeline = Deliveries.CreatePipeline();
    }
}
