using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Journaling;

internal sealed class JournaledStateManagerShared
{
    public JournaledStateManagerShared(
        ILogger<JournaledStateManager> logger,
        IOptions<JournaledStateManagerOptions> options,
        TimeProvider timeProvider,
        IJournalStorage storage,
        IServiceProvider serviceProvider)
        : this(logger, CreateOptions(options), timeProvider, storage, serviceProvider)
    {
    }

    private JournaledStateManagerShared(
        ILogger<JournaledStateManager> logger,
        JournaledStateManagerOptions options,
        TimeProvider timeProvider,
        IJournalStorage storage,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        Logger = logger;
        Options = options;
        TimeProvider = timeProvider;
        Storage = storage;
        ServiceProvider = serviceProvider;
        JournalFormat = JournalFormatServices.GetRequiredJournalFormat(serviceProvider, options.JournalFormatKey);
    }

    public ILogger<JournaledStateManager> Logger { get; }

    public JournaledStateManagerOptions Options { get; }

    public TimeProvider TimeProvider { get; }

    public IJournalStorage Storage { get; }

    public IServiceProvider ServiceProvider { get; }

    public IJournalFormat JournalFormat { get; }

    public string JournalFormatKey => Options.JournalFormatKey;

    public TimeSpan RetirementGracePeriod => Options.RetirementGracePeriod;

    private static JournaledStateManagerOptions CreateOptions(IOptions<JournaledStateManagerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        ArgumentNullException.ThrowIfNull(value);
        return new()
        {
            JournalFormatKey = JournalFormatServices.ValidateJournalFormatKey(value.JournalFormatKey),
            RetirementGracePeriod = value.RetirementGracePeriod,
        };
    }
}
