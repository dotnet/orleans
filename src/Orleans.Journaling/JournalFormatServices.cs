using Microsoft.Extensions.DependencyInjection;

namespace Orleans.Journaling;

internal static class JournalFormatServices
{
    public const string JournalFormatKeyServiceKey = "Orleans.Journaling.JournalFormatKey";

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
                + "Register the physical journal format and durable codec provider family using the same journal format key.");
        }

        return service;
    }

    public static object GetOperationCodec(IStateResolver resolver, IJournaledState state)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(state);

        return resolver is IJournalOperationCodecResolver operationCodecResolver
            ? operationCodecResolver.GetOperationCodec(state)
            : state.OperationCodec;
    }
}
