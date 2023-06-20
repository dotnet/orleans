using Orleans.Runtime;

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
/// Google Cloud Firestore options
/// </summary>
public class FirestoreOptions : GoogleCloudOptions
{
    /// <summary>
    /// The Google Cloud Firestore root collection name.
    /// </summary>
    public string RootCollectionName { get; set; } = "Orleans";

    internal void Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(this.RootCollectionName))
            throw new OrleansConfigurationException("RootCollectionName is required.");

        if (Utils.ForbiddenIdRegex().IsMatch(this.RootCollectionName))
            throw new OrleansConfigurationException(
                $"The RootCollectionName '{this.RootCollectionName}' contains invalid characters.");

        if (string.IsNullOrWhiteSpace(this.ProjectId))
            throw new OrleansConfigurationException("ProjectId is required.");

        if (Utils.ForbiddenIdRegex().IsMatch(this.ProjectId))
            throw new OrleansConfigurationException($"The ProjectId '{this.ProjectId}' contains invalid characters.");
    }
}

public class FirestoreOptionsValidator<TOptions> : IConfigurationValidator where TOptions : FirestoreOptions
{
    public FirestoreOptionsValidator(TOptions options, string? name = null)
    {
        Options = options;
        Name = name;
    }

    public TOptions Options { get; }
    public string? Name { get; }

    public virtual void ValidateConfiguration()
    {
        Options.Validate(this.Name);
    }
}