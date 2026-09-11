using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans;

/// <summary>
/// Extension methods for resolving grain references after compatible implementations become available.
/// </summary>
public static class GrainFactoryResolutionExtensions
{
    /// <summary>
    /// Waits until the cluster manifest contains a compatible implementation of <typeparamref name="TGrainInterface"/>.
    /// </summary>
    /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
    /// <param name="grainFactory">The grain factory.</param>
    /// <param name="grainClassNamePrefix">An optional grain implementation class name prefix.</param>
    /// <param name="cancellationToken">A token which cancels the wait.</param>
    /// <returns>The resolved grain type.</returns>
    public static ValueTask<GrainType> WaitForGrainTypeAsync<TGrainInterface>(
        this IGrainFactory grainFactory,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default)
        where TGrainInterface : IGrain
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        return GetAvailability(grainFactory)
            .WaitForGrainTypeAsync(typeof(TGrainInterface), grainClassNamePrefix, cancellationToken);
    }

    /// <summary>
    /// Returns a grain reference after a compatible implementation becomes available.
    /// </summary>
    /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
    /// <param name="grainFactory">The grain factory.</param>
    /// <param name="primaryKey">The primary key of the grain.</param>
    /// <param name="grainClassNamePrefix">An optional grain implementation class name prefix.</param>
    /// <param name="cancellationToken">A token which cancels the wait.</param>
    /// <returns>A grain reference with a concrete grain identity.</returns>
    public static async ValueTask<TGrainInterface> GetGrainAsync<TGrainInterface>(
        this IGrainFactory grainFactory,
        Guid primaryKey,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default)
        where TGrainInterface : IGrainWithGuidKey
    {
        var grainType = await WaitForGrainTypeAsync<TGrainInterface>(grainFactory, grainClassNamePrefix, cancellationToken);
        return grainFactory.GetGrain<TGrainInterface>(GrainId.Create(grainType, GrainIdKeyExtensions.CreateGuidKey(primaryKey)));
    }

    /// <summary>
    /// Returns a grain reference after a compatible implementation becomes available.
    /// </summary>
    /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
    /// <param name="grainFactory">The grain factory.</param>
    /// <param name="primaryKey">The primary key of the grain.</param>
    /// <param name="grainClassNamePrefix">An optional grain implementation class name prefix.</param>
    /// <param name="cancellationToken">A token which cancels the wait.</param>
    /// <returns>A grain reference with a concrete grain identity.</returns>
    public static async ValueTask<TGrainInterface> GetGrainAsync<TGrainInterface>(
        this IGrainFactory grainFactory,
        long primaryKey,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default)
        where TGrainInterface : IGrainWithIntegerKey
    {
        var grainType = await WaitForGrainTypeAsync<TGrainInterface>(grainFactory, grainClassNamePrefix, cancellationToken);
        return grainFactory.GetGrain<TGrainInterface>(GrainId.Create(grainType, GrainIdKeyExtensions.CreateIntegerKey(primaryKey)));
    }

    /// <summary>
    /// Returns a grain reference after a compatible implementation becomes available.
    /// </summary>
    /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
    /// <param name="grainFactory">The grain factory.</param>
    /// <param name="primaryKey">The primary key of the grain.</param>
    /// <param name="grainClassNamePrefix">An optional grain implementation class name prefix.</param>
    /// <param name="cancellationToken">A token which cancels the wait.</param>
    /// <returns>A grain reference with a concrete grain identity.</returns>
    public static async ValueTask<TGrainInterface> GetGrainAsync<TGrainInterface>(
        this IGrainFactory grainFactory,
        string primaryKey,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default)
        where TGrainInterface : IGrainWithStringKey
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryKey);
        var grainType = await WaitForGrainTypeAsync<TGrainInterface>(grainFactory, grainClassNamePrefix, cancellationToken);
        return grainFactory.GetGrain<TGrainInterface>(GrainId.Create(grainType, IdSpan.Create(primaryKey)));
    }

    /// <summary>
    /// Returns a grain reference after a compatible implementation becomes available.
    /// </summary>
    /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
    /// <param name="grainFactory">The grain factory.</param>
    /// <param name="primaryKey">The primary key of the grain.</param>
    /// <param name="keyExtension">The key extension of the grain.</param>
    /// <param name="grainClassNamePrefix">An optional grain implementation class name prefix.</param>
    /// <param name="cancellationToken">A token which cancels the wait.</param>
    /// <returns>A grain reference with a concrete grain identity.</returns>
    public static async ValueTask<TGrainInterface> GetGrainAsync<TGrainInterface>(
        this IGrainFactory grainFactory,
        Guid primaryKey,
        string keyExtension,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default)
        where TGrainInterface : IGrainWithGuidCompoundKey
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyExtension);
        var grainType = await WaitForGrainTypeAsync<TGrainInterface>(grainFactory, grainClassNamePrefix, cancellationToken);
        return grainFactory.GetGrain<TGrainInterface>(GrainId.Create(grainType, GrainIdKeyExtensions.CreateGuidKey(primaryKey, keyExtension)));
    }

    /// <summary>
    /// Returns a grain reference after a compatible implementation becomes available.
    /// </summary>
    /// <typeparam name="TGrainInterface">The grain interface type.</typeparam>
    /// <param name="grainFactory">The grain factory.</param>
    /// <param name="primaryKey">The primary key of the grain.</param>
    /// <param name="keyExtension">The key extension of the grain.</param>
    /// <param name="grainClassNamePrefix">An optional grain implementation class name prefix.</param>
    /// <param name="cancellationToken">A token which cancels the wait.</param>
    /// <returns>A grain reference with a concrete grain identity.</returns>
    public static async ValueTask<TGrainInterface> GetGrainAsync<TGrainInterface>(
        this IGrainFactory grainFactory,
        long primaryKey,
        string keyExtension,
        string? grainClassNamePrefix = null,
        CancellationToken cancellationToken = default)
        where TGrainInterface : IGrainWithIntegerCompoundKey
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyExtension);
        var grainType = await WaitForGrainTypeAsync<TGrainInterface>(grainFactory, grainClassNamePrefix, cancellationToken);
        return grainFactory.GetGrain<TGrainInterface>(GrainId.Create(grainType, GrainIdKeyExtensions.CreateIntegerKey(primaryKey, keyExtension)));
    }

    private static IGrainTypeAvailability GetAvailability(IGrainFactory grainFactory)
        => grainFactory as IGrainTypeAvailability
            ?? throw new NotSupportedException($"{grainFactory.GetType()} does not support asynchronous grain type availability.");
}
