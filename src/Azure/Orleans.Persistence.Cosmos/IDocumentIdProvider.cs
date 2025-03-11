// <copyright file="IDocumentIdProvider.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Gets document and partition identifiers for grain state documents.
/// </summary>
public interface IDocumentIdProvider
{
    /// <summary>
    /// Gets the document identifier for the specified grain.
    /// </summary>
    /// <param name="stateName">The grain state name.</param>
    /// <param name="grainTypeName">The grain type name.</param>
    /// <param name="grainKey">The grain key.</param>
    /// <returns>The document id and partition key.</returns>
    (string DocumentId, string PartitionKey) GetDocumentIdentifiers(string stateName, string grainTypeName, string grainKey);
}