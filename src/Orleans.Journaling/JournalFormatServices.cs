using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

internal static class JournalFormatServices
{
    public static string ValidateJournalFormatKey(string? journalFormatKey)
    {
        if (string.IsNullOrWhiteSpace(journalFormatKey))
        {
            throw new InvalidOperationException("The configured journal format key must be non-empty.");
        }

        return journalFormatKey;
    }

    public static T GetRequiredKeyedService<T>(IServiceProvider serviceProvider, string journalFormatKey)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        journalFormatKey = ValidateJournalFormatKey(journalFormatKey);

        var service = serviceProvider.GetKeyedService<T>(journalFormatKey);
        if (service is null)
        {
            throw new InvalidOperationException(
                $"Journal format key '{journalFormatKey}' requires keyed service '{typeof(T).FullName}', but none was registered. "
                + "Register the physical journal format and keyed durable operation codecs using the same journal format key.");
        }

        return service;
    }

    public static T GetRequiredOperationCodec<T>(IServiceProvider serviceProvider, string journalFormatKey)
        where T : notnull
        => (T)GetRequiredOperationCodec(serviceProvider, journalFormatKey, typeof(T));

    public static object GetRequiredOperationCodec(IServiceProvider serviceProvider, string journalFormatKey, Type operationCodecServiceType)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(operationCodecServiceType);
        journalFormatKey = ValidateJournalFormatKey(journalFormatKey);

        if (serviceProvider is not IKeyedServiceProvider keyedServiceProvider)
        {
            throw new InvalidOperationException("The configured service provider does not support keyed services.");
        }

        var service = keyedServiceProvider.GetKeyedService(operationCodecServiceType, journalFormatKey);
        if (service is null)
        {
            throw new InvalidOperationException(
                $"Journal format key '{journalFormatKey}' requires keyed operation codec service '{operationCodecServiceType.FullName}', but none was registered. "
                + "Register the physical journal format and durable operation codecs using the same journal format key.");
        }

        return service;
    }

    public static object GetCurrentOperationCodec(IJournaledState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state is IJournaledStateOperationCodecProvider operationCodecProvider)
        {
            return operationCodecProvider.OperationCodec;
        }

        throw new InvalidOperationException(
            $"State '{state.GetType().FullName}' does not expose a cached operation codec. "
            + $"Resolve '{state.OperationCodecServiceType.FullName}' from keyed services instead.");
    }

    public static IJournalFormat GetRequiredJournalFormat(IServiceProvider serviceProvider, string journalFormatKey)
    {
        var format = GetRequiredKeyedService<IJournalFormat>(serviceProvider, journalFormatKey);
        var formatKey = ValidateJournalFormatKey(format.FormatKey);
        if (!string.Equals(formatKey, journalFormatKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Journal format key '{journalFormatKey}' resolved format '{format.GetType().FullName}', but its {nameof(IJournalFormat.FormatKey)} is '{formatKey}'. " +
                $"Register the journal format using the same key it reports.");
        }

        return format;
    }
}
