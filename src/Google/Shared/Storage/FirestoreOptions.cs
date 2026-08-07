using Orleans.Runtime;

#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.GoogleFirestore;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.GoogleFirestore;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.GoogleFirestore;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.GoogleFirestore;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

/// <summary>
/// Google Cloud Firestore options
/// </summary>
public class FirestoreOptions
{
    /// <summary>
    /// The Google Cloud project id.
    /// </summary>
    public string ProjectId { get; set; } = default!;

    /// <summary>
    /// The Firestore emulator host. Leave unset to use Google Cloud Firestore.
    /// </summary>
    public string? EmulatorHost { get; set; }

    /// <summary>
    /// The Google Cloud Firestore root collection name.
    /// </summary>
    public string RootCollectionName { get; set; } = "Orleans";

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(this.RootCollectionName))
            throw new OrleansConfigurationException("RootCollectionName is required.");

        if (Utils.ForbiddenIdRegex().IsMatch(this.RootCollectionName))
            throw new OrleansConfigurationException(
                $"The RootCollectionName '{this.RootCollectionName}' contains invalid characters.");

        if (this.RootCollectionName.Contains('/', StringComparison.Ordinal))
            throw new OrleansConfigurationException("RootCollectionName must be a single Firestore collection identifier.");

        if (string.IsNullOrWhiteSpace(this.ProjectId))
            throw new OrleansConfigurationException("ProjectId is required.");

        if (Utils.ForbiddenIdRegex().IsMatch(this.ProjectId))
            throw new OrleansConfigurationException($"The ProjectId '{this.ProjectId}' contains invalid characters.");
    }
}

internal sealed class FirestoreOptionsValidator<TOptions> : IConfigurationValidator where TOptions : FirestoreOptions
{
    private readonly TOptions _options;

    public FirestoreOptionsValidator(TOptions options) => _options = options;

    public void ValidateConfiguration() => _options.Validate();
}