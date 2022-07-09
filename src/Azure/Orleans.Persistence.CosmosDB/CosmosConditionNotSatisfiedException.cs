using System.Runtime.Serialization;
using Orleans.Storage;

namespace Orleans.Persistence.CosmosDB;

/// <summary>
/// Exception thrown when a storage provider detects an Etag inconsistency when attempting to perform a WriteStateAsync operation.
/// </summary>
[Serializable]
public class CosmosConditionNotSatisfiedException : InconsistentStateException
{
    private const string DefaultMessageFormat = "Cosmos DB condition not Satisfied.  GrainType: {0}, GrainId: {1}, TableName: {2}, StoredETag: {3}, CurrentETag: {4}";

    /// <summary>
    /// Exception thrown when a cosmos DB exception is thrown due to update conditions not being satisfied.
    /// </summary>
    public CosmosConditionNotSatisfiedException(
        string errorMsg,
        string grainType,
        GrainId grainId,
        string collection,
        string storedEtag,
        string currentEtag,
        Exception storageException)
        : base(errorMsg, storedEtag, currentEtag, storageException)
    {
        this.GrainType = grainType;
        this.GrainId = grainId.ToString();
        this.Collection = collection;
    }

    /// <summary>
    /// Exception thrown when a cosmos DB exception is thrown due to update conditions not being satisfied.
    /// </summary>
    public CosmosConditionNotSatisfiedException(
        string grainType,
        GrainId grainId,
        string collection,
        string storedEtag,
        string currentEtag,
        Exception storageException)
        : this(CreateDefaultMessage(grainType, grainId, collection, storedEtag, currentEtag), grainType, grainId, collection, storedEtag, currentEtag, storageException)
    {
    }

    /// <summary>
    /// Id of grain
    /// </summary>
    public string GrainId { get; } = default!;

    /// <summary>
    /// Type of grain that throw this exception
    /// </summary>
    public string GrainType { get; } = default!;

    /// <summary>
    /// The collection name
    /// </summary>
    public string Collection { get; } = default!;

    /// <summary>
    /// Exception thrown when a cosmos DB exception is thrown due to update conditions not being satisfied.
    /// </summary>
    public CosmosConditionNotSatisfiedException()
    {
    }

    /// <summary>
    /// Exception thrown when a cosmos DB exception is thrown due to update conditions not being satisfied.
    /// </summary>
    public CosmosConditionNotSatisfiedException(string msg)
        : base(msg)
    {
    }

    /// <summary>
    /// Exception thrown when a cosmos DB exception is thrown due to update conditions not being satisfied.
    /// </summary>
    public CosmosConditionNotSatisfiedException(string msg, Exception exc)
        : base(msg, exc)
    {
    }

    private static string CreateDefaultMessage(
        string grainType,
        GrainId grainId,
        string collection,
        string storedEtag,
        string currentEtag) => string.Format(DefaultMessageFormat, grainType, grainId, collection, storedEtag, currentEtag);

    /// <summary>
    /// Exception thrown when a cosmos DB exception is thrown due to update conditions not being satisfied.
    /// </summary>
    protected CosmosConditionNotSatisfiedException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        this.GrainType = info.GetString("GrainType")!;
        this.GrainId = info.GetString("GrainId")!;
        this.Collection = info.GetString("Collection")!;
    }

    /// <inheritdoc />
    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        if (info == null) throw new ArgumentNullException(nameof(info));

        info.AddValue("GrainType", this.GrainType);
        info.AddValue("GrainId", this.GrainId);
        info.AddValue("Collection", this.Collection);
        base.GetObjectData(info, context);
    }
}