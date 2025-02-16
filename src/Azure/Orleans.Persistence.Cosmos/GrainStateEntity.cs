// <copyright file="GrainStateEntity.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// The entity stored in Cosmos DB.
/// </summary>
/// <typeparam name="TState">
/// The underlying state type.
/// </typeparam>
internal sealed class ExperimentalGrainStateEntity<TState>
{
    /// <summary>
    /// Gets or sets the entity ID.
    /// </summary>
    [Newtonsoft.Json.JsonProperty("id")]
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    /// <summary>
    /// Gets or sets the parititon key.
    /// </summary>
    [Newtonsoft.Json.JsonProperty("pk")]
    [System.Text.Json.Serialization.JsonPropertyName("pk")]
    public string PartitionKey { get; set; } = default!;

    /// <summary>
    /// Gets or sets the parititon key.
    /// </summary>
    [Newtonsoft.Json.JsonProperty("type")]
    [System.Text.Json.Serialization.JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    /// <summary>
    /// Gets or sets the item TTL.
    /// </summary>
    [Newtonsoft.Json.JsonProperty("ttl", DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.IgnoreAndPopulate)]
    [System.Text.Json.Serialization.JsonPropertyName("ttl")]
    [System.Text.Json.Serialization.JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Ttl { get; set; } = default!;

    /// <summary>
    /// Gets or sets the state.
    /// </summary>
    [Newtonsoft.Json.JsonProperty("state")]
    [System.Text.Json.Serialization.JsonPropertyName("state")]
    public TState State { get; set; } = default!;

    /// <summary>
    /// Gets or sets the ETag.
    /// </summary>
    [Newtonsoft.Json.JsonProperty("_etag")]
    [JsonPropertyName("_etag")]
    public string ETag { get; set; } = default!;
}

internal sealed class GrainStateEntity<TState>
{
    internal const string ID_FIELD = "id";
    internal const string ETAG_FIELD = "_etag";

    [Newtonsoft.Json.JsonProperty(nameof(GrainType))]
    [System.Text.Json.Serialization.JsonPropertyName(nameof(GrainType))]
    public string GrainType { get; set; } = default!;

    [Newtonsoft.Json.JsonProperty(nameof(State))]
    [System.Text.Json.Serialization.JsonPropertyName(nameof(State))]
    public TState State { get; set; } = default!;

    [Newtonsoft.Json.JsonProperty(nameof(PartitionKey))]
    [System.Text.Json.Serialization.JsonPropertyName(nameof(PartitionKey))]
    public string PartitionKey { get; set; } = default!;

    [Newtonsoft.Json.JsonProperty(ID_FIELD)]
    [System.Text.Json.Serialization.JsonPropertyName(ID_FIELD)]
    public string Id { get; set; } = default!;

    [Newtonsoft.Json.JsonProperty(ETAG_FIELD)]
    [System.Text.Json.Serialization.JsonPropertyName(ETAG_FIELD)]
    public string ETag { get; set; } = default!;
}