using System;
using Microsoft.Extensions.Options;

namespace Orleans.Configuration;

internal sealed class DisseminationOptionsValidator : IValidateOptions<DisseminationOptions>
{
    private static readonly TimeSpan MaxPeriodicTimerPeriod = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    public ValidateOptionsResult Validate(string? name, DisseminationOptions options)
    {
        if (options.MaxConcurrentSends <= 0)
        {
            return ValidateOptionsResult.Fail($"{nameof(DisseminationOptions.MaxConcurrentSends)} must be greater than 0.");
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

        if (overlay.AntiEntropyInterval < TimeSpan.FromMilliseconds(1)
            || overlay.AntiEntropyInterval > MaxPeriodicTimerPeriod)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(DisseminationOverlayOptions.AntiEntropyInterval)} must be between 1 millisecond and {MaxPeriodicTimerPeriod}.");
        }

        if (overlay.AntiEntropyPeerCount <= 0)
        {
            return ValidateOptionsResult.Fail($"{nameof(DisseminationOverlayOptions.AntiEntropyPeerCount)} must be greater than 0.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal sealed class DisseminationNamespaceOptionsValidator
{
    private static readonly TimeSpan MaxTimerPeriod = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    public static ValidateOptionsResult Validate(string owner, DisseminationNamespaceOptions options)
    {
        if (options.MaxPendingItemCount <= 0)
        {
            return ValidateOptionsResult.Fail($"{owner}.{nameof(DisseminationNamespaceOptions.MaxPendingItemCount)} must be greater than 0.");
        }

        if (options.MaxCoalescingDelay < TimeSpan.FromMilliseconds(1)
            || options.MaxCoalescingDelay > MaxTimerPeriod)
        {
            return ValidateOptionsResult.Fail(
                $"{owner}.{nameof(DisseminationNamespaceOptions.MaxCoalescingDelay)} must be between 1 millisecond and {MaxTimerPeriod}.");
        }

        if (options.StaleItemTtl <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{owner}.{nameof(DisseminationNamespaceOptions.StaleItemTtl)} must be greater than 0.");
        }

        if (options.ExpectedUpdateCadence <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail($"{owner}.{nameof(DisseminationNamespaceOptions.ExpectedUpdateCadence)} must be greater than 0.");
        }

        if (options.MaxPayloadBytes <= 0)
        {
            return ValidateOptionsResult.Fail($"{owner}.{nameof(DisseminationNamespaceOptions.MaxPayloadBytes)} must be greater than 0.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal sealed class DeploymentLoadPublisherOptionsValidator : IValidateOptions<DeploymentLoadPublisherOptions>
{
    public ValidateOptionsResult Validate(string? name, DeploymentLoadPublisherOptions options) =>
        DisseminationNamespaceOptionsValidator.Validate(nameof(DeploymentLoadPublisherOptions.Dissemination), options.Dissemination);
}

internal sealed class ClusterMembershipOptionsDisseminationValidator : IValidateOptions<ClusterMembershipOptions>
{
    public ValidateOptionsResult Validate(string? name, ClusterMembershipOptions options) =>
        DisseminationNamespaceOptionsValidator.Validate(nameof(ClusterMembershipOptions.Dissemination), options.Dissemination);
}
