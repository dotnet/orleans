using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

internal static class LogFormatServices
{
    public const string LogFormatKeyServiceKey = "Orleans.Journaling.LogFormatKey";

    public static string ValidateLogFormatKey(string? logFormatKey)
    {
        if (string.IsNullOrWhiteSpace(logFormatKey))
        {
            throw new InvalidOperationException("The configured log format key must be non-empty.");
        }

        return logFormatKey;
    }

    public static T GetRequiredKeyedService<T>(IServiceProvider serviceProvider, string logFormatKey)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        logFormatKey = ValidateLogFormatKey(logFormatKey);

        var service = serviceProvider.GetKeyedService<T>(logFormatKey);
        if (service is null)
        {
            throw new InvalidOperationException(
                $"Journaling log format key '{logFormatKey}' requires keyed service '{typeof(T).FullName}', but none was registered. "
                + "Register the physical log format and durable codec provider family using the same log format key.");
        }

        return service;
    }
}
