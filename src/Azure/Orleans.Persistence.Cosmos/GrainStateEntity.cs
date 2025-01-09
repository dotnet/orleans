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
internal sealed class GrainStateEntity<TState>
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
    [System.Text.Json.Serialization.JsonPropertyName("_etag")]
    public string ETag { get; set; } = default!;
}