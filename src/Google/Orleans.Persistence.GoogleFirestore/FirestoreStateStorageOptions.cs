using Orleans.Runtime;
using Orleans.Storage;


namespace Orleans.Persistence.GoogleFirestore;

public class FirestoreStateStorageOptions : FirestoreOptions, IStorageProviderSerializerOptions
{
    /// <summary>
    /// Indicates if grain data should be deleted or reset to defaults when a grain clears its state.
    /// </summary>
    public bool DeleteStateOnClear { get; set; }

    /// <inheritdoc/>
    public IGrainStorageSerializer GrainStorageSerializer { get; set; } = default!;
}
