using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Journaling;

internal sealed class JournaledStateManagerShared
{
    public JournaledStateManagerShared(
        ILogger<JournaledStateManager> logger,
        IOptions<JournaledStateManagerOptions> options,
        TimeProvider timeProvider,
        IServiceProvider serviceProvider)
        : this(logger, CreateOptions(options, serviceProvider), timeProvider)
    {
    }

    private JournaledStateManagerShared(
        ILogger<JournaledStateManager> logger,
        JournaledStateManagerOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Logger = logger;
        Options = options;
        TimeProvider = timeProvider;
    }

    public ILogger<JournaledStateManager> Logger { get; }

    public JournaledStateManagerOptions Options { get; }

    public TimeProvider TimeProvider { get; }

    public string JournalFormatKey => Options.JournalFormatKey;

    public TimeSpan RetirementGracePeriod => Options.RetirementGracePeriod;

    internal static JournaledStateManagerShared CreateForTests(
        ILogger<JournaledStateManager> logger,
        IOptions<JournaledStateManagerOptions> options,
        TimeProvider timeProvider,
        string? journalFormatKey)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new(
            logger,
            CreateOptions(options.Value, journalFormatKey ?? options.Value.JournalFormatKey),
            timeProvider);
    }

    private static JournaledStateManagerOptions CreateOptions(
        IOptions<JournaledStateManagerOptions> options,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return CreateOptions(options.Value, GetJournalFormatKey(serviceProvider, options.Value.JournalFormatKey));
    }

    private static JournaledStateManagerOptions CreateOptions(JournaledStateManagerOptions options, string journalFormatKey)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new()
        {
            JournalFormatKey = JournalFormatServices.ValidateJournalFormatKey(journalFormatKey),
            RetirementGracePeriod = options.RetirementGracePeriod,
        };
    }

    private static string GetJournalFormatKey(IServiceProvider serviceProvider, string configuredJournalFormatKey)
    {
        var journalFormatKeyProvider = serviceProvider.GetService<IJournalFormatKeyProvider>();
        if (journalFormatKeyProvider is null && serviceProvider.GetService<IJournalStorageProvider>() is IJournalFormatKeyProvider storageProvider)
        {
            journalFormatKeyProvider = storageProvider;
        }

        if (journalFormatKeyProvider is null)
        {
            return configuredJournalFormatKey;
        }

        return journalFormatKeyProvider.GetJournalFormatKey(serviceProvider.GetRequiredService<IGrainContext>());
    }
}
