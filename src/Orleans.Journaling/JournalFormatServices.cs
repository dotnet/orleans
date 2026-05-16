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
                + "Register the physical journal format and keyed durable command codecs using the same journal format key.");
        }

        return service;
    }

    public static T GetRequiredCommandCodec<T>(IServiceProvider serviceProvider, string journalFormatKey)
        where T : notnull
        => GetRequiredKeyedService<T>(serviceProvider, journalFormatKey);

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
