using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Orleans.Journaling;

internal sealed class JournaledStateManagerShared
{
    public JournaledStateManagerShared(
        ILogger<JournaledStateManager> logger,
        IOptions<JournaledStateManagerOptions> options,
        TimeProvider timeProvider)
        : this(logger, CreateOptions(options), timeProvider)
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
