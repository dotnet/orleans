using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Journaling;

internal sealed class JournaledStateManagerShared
{
    public JournaledStateManagerShared(
        ILogger<JournaledStateManager> logger,
        IOptions<JournaledStateManagerOptions> options,
        TimeProvider timeProvider,
        IServiceProvider serviceProvider,
        JournalingInstruments? instruments = null)
        : this(logger, CreateOptions(options), timeProvider, instruments, serviceProvider)
    {
    }

    private JournaledStateManagerShared(
        ILogger<JournaledStateManager> logger,
        JournaledStateManagerOptions options,
        TimeProvider timeProvider,
        JournalingInstruments? instruments,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        Logger = logger;
        Options = options;
        TimeProvider = timeProvider;
        Instruments = instruments ?? JournalingInstruments.CreateForDirectConstruction();
        ServiceProvider = serviceProvider;
        JournalFormat = JournalFormatServices.GetRequiredJournalFormat(serviceProvider, options.JournalFormatKey);
    }

    public ILogger<JournaledStateManager> Logger { get; }

    public JournaledStateManagerOptions Options { get; }

    public TimeProvider TimeProvider { get; }
    public JournalingInstruments Instruments { get; }

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
