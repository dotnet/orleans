using System.Runtime.Serialization;
using Orleans.Storage;

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Exception thrown when a storage provider detects an Etag inconsistency when attempting to perform a WriteStateAsync operation.
/// </summary>
[Serializable]
[GenerateSerializer]
public class CosmosConditionNotSatisfiedException : InconsistentStateException
{
    private const string DefaultMessageFormat = "Cosmos DB condition not satisfied. GrainType: {0}, GrainId: {1}, TableName: {2}, StoredETag: {3}, CurrentETag: {4}";

    /// <summary>
    /// Exception thrown when a Cosmos DB exception is thrown due to update conditions not being satisfied.
    /// </summary>
    public CosmosConditionNotSatisfiedException(
        string errorMsg,
        string grainType,
        GrainId grainId,
        string collection,
        string storedEtag,
        string currentEtag)
        : base(errorMsg, storedEtag, currentEtag)
    {
        GrainType = grainType;
        GrainId = grainId.ToString();
        Collection = collection;
    }

    /// <summary>
    /// Exception thrown when a Cosmos DB exception is thrown due to update conditions not being satisfied.
    /// </summary>
    public CosmosConditionNotSatisfiedException(
        string grainType,
        GrainId grainId,
        string collection,
        string storedEtag,
        string currentEtag)
        : this(CreateDefaultMessage(grainType, grainId, collection, storedEtag, currentEtag), grainType, grainId, collection, storedEtag, currentEtag)
    {
    }

    /// <summary>
    /// Gets the id of the affected grain.
    /// </summary>
    [Id(0)]
    public string GrainId { get; } = default!;

    /// <summary>
    /// Gets the grain type of the affected grain.
    /// </summary>
    [Id(1)]
    public string GrainType { get; } = default!;

    /// <summary>
    /// Gets the collection name
    /// </summary>
    [Id(2)]
    public string Collection { get; } = default!;

    /// <summary>
    /// Exception thrown when a Cosmos DB exception is thrown due to update conditions not being satisfied.
    /// </summary>
    public CosmosConditionNotSatisfiedException()
    {
    }

    /// <summary>
    /// Exception thrown when a Cosmos DB exception is thrown due to update conditions not being satisfied.
    /// </summary>
    public CosmosConditionNotSatisfiedException(string msg)
        : base(msg)
    {
    }

    /// <summary>
    /// Exception thrown when a Cosmos DB exception is thrown due to update conditions not being satisfied.
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
    /// Exception thrown when a Cosmos DB exception is thrown due to update conditions not being satisfied.
    /// </summary>
    protected CosmosConditionNotSatisfiedException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        GrainType = info.GetString("GrainType")!;
        GrainId = info.GetString("GrainId")!;
        Collection = info.GetString("Collection")!;
    }

    /// <inheritdoc />
    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        if (info == null) throw new ArgumentNullException(nameof(info));

        info.AddValue("GrainType", GrainType);
        info.AddValue("GrainId", GrainId);
        info.AddValue("Collection", Collection);
        base.GetObjectData(info, context);
    }
}