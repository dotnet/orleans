using Microsoft.Extensions.Options;

namespace Orleans.Configuration
{
    internal class SiloMessagingOptionsValidator : IValidateOptions<SiloMessagingOptions>
    {
        public ValidateOptionsResult Validate(string name, SiloMessagingOptions options)
        {
            if (options.MaxForwardCount > 255)
            {
                return ValidateOptionsResult.Fail($"Value for {nameof(SiloMessagingOptions)}.{nameof(SiloMessagingOptions.MaxForwardCount)} must not be greater than 255.");
            }

            return ValidateOptionsResult.Success;
        }
    }
}
