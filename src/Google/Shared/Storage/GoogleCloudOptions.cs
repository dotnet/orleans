#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.GoogleFirestore;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.GoogleFirestore;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.GoogleFirestore;
#elif GOOGLE_TESTS
namespace Orleans.Tests.GoogleFirestore;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.GoogleFirestore;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

/// <summary>
/// Basic options for Google Cloud providers
/// </summary>
public class GoogleCloudOptions
{
    /// <summary>
    /// The Google Cloud project Id.
    /// </summary>
    public string ProjectId { get; set; } = default!;

    /// <summary>
    /// Set this to the host of the emulator if you want to use the emulator.
    /// This is useful only for tests.
    /// </summary>
    public string? EmulatorHost { get; set; }
}