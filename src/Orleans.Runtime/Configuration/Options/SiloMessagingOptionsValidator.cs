using Microsoft.Extensions.Options;

namespace Orleans.Configuration;

internal class SiloMessagingOptionsValidator : IValidateOptions<SiloMessagingOptions>
{
    public ValidateOptionsResult Validate(string? name, SiloMessagingOptions options)
    {
        if (options.MaxForwardCount > 255)
        {
            return ValidateOptionsResult.Fail($"Value for {nameof(SiloMessagingOptions)}.{nameof(SiloMessagingOptions.MaxForwardCount)} must not be greater than 255.");
        }

        if (options.PlacementTimeout < TimeSpan.FromMilliseconds(10) || options.PlacementTimeout > TimeSpan.FromDays(1))
        {
            return ValidateOptionsResult.Fail($"Value for {nameof(SiloMessagingOptions)}.{nameof(SiloMessagingOptions.PlacementTimeout)} must be between 10 milliseconds and 1 day.");
        }

        if (options.PlacementMaxRetries < 0)
        {
            return ValidateOptionsResult.Fail($"Value for {nameof(SiloMessagingOptions)}.{nameof(SiloMessagingOptions.PlacementMaxRetries)} must not be negative.");
        }

        if (options.PlacementRetryBaseDelay < TimeSpan.Zero || options.PlacementRetryBaseDelay > TimeSpan.FromDays(1))
        {
            return ValidateOptionsResult.Fail($"Value for {nameof(SiloMessagingOptions)}.{nameof(SiloMessagingOptions.PlacementRetryBaseDelay)} must be between 0 and 1 day.");
        }

        return ValidateOptionsResult.Success;
    }
}
