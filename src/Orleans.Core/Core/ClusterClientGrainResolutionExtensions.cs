using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace Orleans;

/// <summary>
/// Extension methods for resolving grain references after compatible implementations become available.
/// </summary>
public static class ClusterClientGrainResolutionExtensions
{
    /// <summary>
    /// Waits until the cluster manifest contains a compatible implementation of <typeparamref name="TGrainInterface"/>.
    /// </summary>
    /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
    /// <param name="client">The cluster client.</param>
    /// <param name="grainClassNamePrefix">An optional grain implementation class name prefix.</param>
    /// <param name="cancellationToken">A token which cancels the wait.</param>
    /// <returns>The resolved grain type.</returns>
    public static async ValueTask<GrainType> WaitForGrainTypeAsync<TGrainInterface>(
        this IClusterClient client,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default)
        where TGrainInterface : IGrain
    {
        ArgumentNullException.ThrowIfNull(client);
        return await client.ServiceProvider
            .GetRequiredService<IGrainTypeAvailability>()
            .WaitForGrainTypeAsync(typeof(TGrainInterface), grainClassNamePrefix, cancellationToken);
    }

    /// <summary>
    /// Returns a grain reference after a compatible implementation becomes available.
    /// </summary>
    /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
    /// <param name="client">The cluster client.</param>
    /// <param name="primaryKey">The primary key of the grain.</param>
    /// <param name="grainClassNamePrefix">An optional grain implementation class name prefix.</param>
    /// <param name="cancellationToken">A token which cancels the wait.</param>
    /// <returns>A grain reference with a concrete grain identity.</returns>
    public static async ValueTask<TGrainInterface> GetGrainAsync<TGrainInterface>(
        this IClusterClient client,
        Guid primaryKey,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default)
        where TGrainInterface : IGrainWithGuidKey
    {
        var grainType = await WaitForGrainTypeAsync<TGrainInterface>(client, grainClassNamePrefix, cancellationToken);
        return client.GetGrain<TGrainInterface>(GrainId.Create(grainType, GrainIdKeyExtensions.CreateGuidKey(primaryKey)));
    }

    /// <summary>
    /// Returns a grain reference after a compatible implementation becomes available.
    /// </summary>
    /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
    /// <param name="client">The cluster client.</param>
    /// <param name="primaryKey">The primary key of the grain.</param>
    /// <param name="grainClassNamePrefix">An optional grain implementation class name prefix.</param>
    /// <param name="cancellationToken">A token which cancels the wait.</param>
    /// <returns>A grain reference with a concrete grain identity.</returns>
    public static async ValueTask<TGrainInterface> GetGrainAsync<TGrainInterface>(
        this IClusterClient client,
        long primaryKey,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default)
        where TGrainInterface : IGrainWithIntegerKey
    {
        var grainType = await WaitForGrainTypeAsync<TGrainInterface>(client, grainClassNamePrefix, cancellationToken);
        return client.GetGrain<TGrainInterface>(GrainId.Create(grainType, GrainIdKeyExtensions.CreateIntegerKey(primaryKey)));
    }

    /// <summary>
    /// Returns a grain reference after a compatible implementation becomes available.
    /// </summary>
    /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
    /// <param name="client">The cluster client.</param>
    /// <param name="primaryKey">The primary key of the grain.</param>
    /// <param name="grainClassNamePrefix">An optional grain implementation class name prefix.</param>
    /// <param name="cancellationToken">A token which cancels the wait.</param>
    /// <returns>A grain reference with a concrete grain identity.</returns>
    public static async ValueTask<TGrainInterface> GetGrainAsync<TGrainInterface>(
        this IClusterClient client,
        string primaryKey,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default)
        where TGrainInterface : IGrainWithStringKey
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryKey);
        var grainType = await WaitForGrainTypeAsync<TGrainInterface>(client, grainClassNamePrefix, cancellationToken);
        return client.GetGrain<TGrainInterface>(GrainId.Create(grainType, IdSpan.Create(primaryKey)));
    }

    /// <summary>
    /// Returns a grain reference after a compatible implementation becomes available.
    /// </summary>
    /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
    /// <param name="client">The cluster client.</param>
    /// <param name="primaryKey">The primary key of the grain.</param>
    /// <param name="keyExtension">The key extension of the grain.</param>
    /// <param name="grainClassNamePrefix">An optional grain implementation class name prefix.</param>
    /// <param name="cancellationToken">A token which cancels the wait.</param>
    /// <returns>A grain reference with a concrete grain identity.</returns>
    public static async ValueTask<TGrainInterface> GetGrainAsync<TGrainInterface>(
        this IClusterClient client,
        Guid primaryKey,
        string keyExtension,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default)
        where TGrainInterface : IGrainWithGuidCompoundKey
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyExtension);
        var grainType = await WaitForGrainTypeAsync<TGrainInterface>(client, grainClassNamePrefix, cancellationToken);
        return client.GetGrain<TGrainInterface>(GrainId.Create(grainType, GrainIdKeyExtensions.CreateGuidKey(primaryKey, keyExtension)));
    }

    /// <summary>
    /// Returns a grain reference after a compatible implementation becomes available.
    /// </summary>
    /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
    /// <param name="client">The cluster client.</param>
    /// <param name="primaryKey">The primary key of the grain.</param>
    /// <param name="keyExtension">The key extension of the grain.</param>
    /// <param name="grainClassNamePrefix">An optional grain implementation class name prefix.</param>
    /// <param name="cancellationToken">A token which cancels the wait.</param>
    /// <returns>A grain reference with a concrete grain identity.</returns>
    public static async ValueTask<TGrainInterface> GetGrainAsync<TGrainInterface>(
        this IClusterClient client,
        long primaryKey,
        string keyExtension,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default)
        where TGrainInterface : IGrainWithIntegerCompoundKey
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyExtension);
        var grainType = await WaitForGrainTypeAsync<TGrainInterface>(client, grainClassNamePrefix, cancellationToken);
        return client.GetGrain<TGrainInterface>(GrainId.Create(grainType, GrainIdKeyExtensions.CreateIntegerKey(primaryKey, keyExtension)));
    }
}
