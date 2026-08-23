using System;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration;

internal sealed class DisseminationOptionsValidator : IValidateOptions<DisseminationOptions>
{
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    public ValidateOptionsResult Validate(string? name, DisseminationOptions options)
    {
        if (options.MaxConcurrentSends <= 0)
        {
            return ValidateOptionsResult.Fail($"{nameof(DisseminationOptions.MaxConcurrentSends)} must be greater than 0.");
        }

        if (options.FailureBackoff <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{nameof(DisseminationOptions.FailureBackoff)} must be greater than 0.");
        }

        if (options.MaxBatchBytes <= 0)
        {
            return ValidateOptionsResult.Fail($"{nameof(DisseminationOptions.MaxBatchBytes)} must be greater than 0.");
        }

        if (options.MaxBatchItems <= 0)
        {
            return ValidateOptionsResult.Fail($"{nameof(DisseminationOptions.MaxBatchItems)} must be greater than 0.");
        }

        var overlay = options.Overlay;
        if (overlay.TargetHopCount <= 0)
        {
            return ValidateOptionsResult.Fail($"{nameof(DisseminationOverlayOptions.TargetHopCount)} must be greater than 0.");
        }

        if (overlay.MinFanOutFactor <= 0)
        {
            return ValidateOptionsResult.Fail($"{nameof(DisseminationOverlayOptions.MinFanOutFactor)} must be greater than 0.");
        }

        if (overlay.MaxFanOutFactor < overlay.MinFanOutFactor)
        {
            return ValidateOptionsResult.Fail($"{nameof(DisseminationOverlayOptions.MaxFanOutFactor)} must be greater than or equal to {nameof(DisseminationOverlayOptions.MinFanOutFactor)}.");
        }

        if (overlay.AntiEntropyInterval <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{nameof(DisseminationOverlayOptions.AntiEntropyInterval)} must be greater than 0.");
        }

        if (overlay.AntiEntropyInterval > MaximumTimerDelay)
        {
            return ValidateOptionsResult.Fail($"{nameof(DisseminationOverlayOptions.AntiEntropyInterval)} must not exceed {MaximumTimerDelay}.");
        }

        if (overlay.AntiEntropyPeerCount <= 0)
        {
            return ValidateOptionsResult.Fail($"{nameof(DisseminationOverlayOptions.AntiEntropyPeerCount)} must be greater than 0.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal sealed class DisseminationTopicOptionsValidator
{
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    public static ValidateOptionsResult Validate(string owner, DisseminationTopicOptions options)
    {
        if (options.MaxPendingItemCount <= 0)
        {
            return ValidateOptionsResult.Fail($"{owner}.{nameof(DisseminationTopicOptions.MaxPendingItemCount)} must be greater than 0.");
        }

        if (options.MaxCoalescingDelay <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{owner}.{nameof(DisseminationTopicOptions.MaxCoalescingDelay)} must be greater than 0.");
        }

        if (options.MaxCoalescingDelay > MaximumTimerDelay)
        {
            return ValidateOptionsResult.Fail($"{owner}.{nameof(DisseminationTopicOptions.MaxCoalescingDelay)} must not exceed {MaximumTimerDelay}.");
        }

        if (options.StaleItemTtl <= options.MaxCoalescingDelay)
        {
            return ValidateOptionsResult.Fail($"{owner}.{nameof(DisseminationTopicOptions.StaleItemTtl)} must be greater than {nameof(DisseminationTopicOptions.MaxCoalescingDelay)}.");
        }

        if (options.ExpectedUpdateCadence <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{owner}.{nameof(DisseminationTopicOptions.ExpectedUpdateCadence)} must be greater than 0.");
        }

        if (options.MaxPayloadBytes <= 0)
        {
            return ValidateOptionsResult.Fail($"{owner}.{nameof(DisseminationTopicOptions.MaxPayloadBytes)} must be greater than 0.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal sealed class DeploymentLoadPublisherOptionsValidator : IValidateOptions<DeploymentLoadPublisherOptions>
{
    public ValidateOptionsResult Validate(string? name, DeploymentLoadPublisherOptions options) =>
        DisseminationTopicOptionsValidator.Validate(nameof(DeploymentLoadPublisherOptions.Dissemination), options.Dissemination);
}

internal sealed class ClusterMembershipOptionsDisseminationValidator : IValidateOptions<ClusterMembershipOptions>
{
    public ValidateOptionsResult Validate(string? name, ClusterMembershipOptions options) =>
        DisseminationTopicOptionsValidator.Validate(nameof(ClusterMembershipOptions.Dissemination), options.Dissemination);
}
