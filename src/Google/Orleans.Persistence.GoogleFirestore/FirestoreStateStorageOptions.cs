using System.Text.Json;
using Orleans.Runtime;


namespace Orleans.Persistence.GoogleFirestore;

public class FirestoreStateStorageOptions : FirestoreOptions
{
    /// <summary>
    /// Indicates if grain data should be deleted or reset to defaults when a grain clears it's state.
    /// </summary>
    public bool DeleteStateOnClear { get; set; }

    /// <summary>
    /// The System.Text.Json serializer options
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; set; } = new();
}

/// <summary>
/// Configuration validator for FirestoreStateStorageOptions
/// </summary>
public class FirestoreStateStorageOptionsValidator : IConfigurationValidator
{
    private readonly FirestoreStateStorageOptions options;
    private readonly string name;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="options">The option to be validated.</param>
    /// <param name="name">The option name to be validated.</param>
    public FirestoreStateStorageOptionsValidator(FirestoreStateStorageOptions options, string name)
    {
        this.options = options;
        this.name = name;
    }

    public void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(this.options.ProjectId))
            throw new OrleansConfigurationException(
                $"Configuration for GoogleFirestoreStorage {this.name} is invalid. {nameof(this.options.ProjectId)} is not valid.");

        if (string.IsNullOrWhiteSpace(this.options.RootCollectionName))
            throw new OrleansConfigurationException(
                $"Configuration for GoogleFirestoreStorage {this.name} is invalid. {nameof(this.options.RootCollectionName)} is not valid.");
    }
}
