#if NET6_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Internal;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Orleans.Serialization.TypeSystem;

namespace Orleans.Serialization.Hosting;

/// <summary>
/// Refreshes the serialization layer after a .NET Hot Reload update: re-runs the updated assemblies'
/// generated manifest providers, merges the result into the live <see cref="TypeManifestOptions"/>, and
/// refreshes the caches derived from it.
/// </summary>
/// <remarks>
/// This assembly has no logging dependency; failures are reported through <see cref="RefreshFailed"/>,
/// which the silo and client refreshers log.
/// </remarks>
internal sealed class SerializationHotReloadRefresher : IHotReloadRefreshParticipant, IDisposable
{
    private readonly object _lock = new();
    private readonly TypeManifestOptions _manifest;
    private readonly CodecProvider _codecProvider;
    private readonly TypeConverter _typeConverter;
    private readonly WellKnownTypeCollection _wellKnownTypes;
    private readonly TypeCodec _typeCodec;

    public SerializationHotReloadRefresher(
        IOptions<TypeManifestOptions> manifest,
        CodecProvider codecProvider,
        TypeConverter typeConverter,
        WellKnownTypeCollection wellKnownTypes,
        TypeCodec typeCodec)
    {
        _manifest = manifest.Value;
        _codecProvider = codecProvider;
        _typeConverter = typeConverter;
        _wellKnownTypes = wellKnownTypes;
        _typeCodec = typeCodec;
        HotReloadMetadataUpdateHandler.Register(this);
    }

    /// <summary>
    /// Raised when a refresh completes successfully.
    /// </summary>
    public event Action? Refreshed;

    /// <summary>
    /// Raised when part of a refresh fails; the message describes the failed step.
    /// </summary>
    public event Action<string, Exception>? RefreshFailed;

    public int Phase => 0;

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "Hot reload manifest providers are generated runtime metadata types and are activated only in metadata-update-capable, non-trimmed development processes.")]
    public void Refresh(HashSet<Assembly>? updatedAssemblies)
    {
        try
        {
            var providers = new List<IConfigureOptions<TypeManifestOptions>>();
            foreach (var assembly in updatedAssemblies ?? (IEnumerable<Assembly>)ReferencedAssemblyProvider.GetRelevantAssemblies())
            {
                ClearGeneratedAccessorCaches(assembly);
                foreach (var attribute in assembly.GetCustomAttributes<TypeManifestProviderAttribute>())
                {
                    try
                    {
                        if (Activator.CreateInstance(attribute.ProviderType, nonPublic: true) is IConfigureOptions<TypeManifestOptions> provider)
                        {
                            providers.Add(provider);
                        }
                    }
                    catch (Exception exception)
                    {
                        OnFailure($"Failed to instantiate type manifest provider '{attribute.ProviderType}'.", exception);
                    }
                }
            }

            Refresh(providers);
        }
        catch (Exception exception)
        {
            OnFailure("Failed to refresh serialization metadata; a restart may be required for the changes to take effect.", exception);
        }
    }

    /// <summary>
    /// Re-runs the provided manifest providers and refreshes all derived state. Each provider is run
    /// against a scratch <see cref="TypeManifestOptions"/> because generated providers use
    /// <see cref="Dictionary{TKey, TValue}.Add(TKey, TValue)"/> for well-known type ids and aliases,
    /// which throws for entries the live options already contain.
    /// </summary>
    internal void Refresh(IEnumerable<IConfigureOptions<TypeManifestOptions>> providers)
    {
        lock (_lock)
        {
            foreach (var provider in providers)
            {
                var scratch = new TypeManifestOptions();
                try
                {
                    provider.Configure(scratch);
                }
                catch (Exception exception)
                {
                    OnFailure($"Failed to run type manifest provider '{provider.GetType()}'.", exception);
                    continue;
                }

                _manifest.MergeFrom(scratch);
            }

            _codecProvider.OnManifestUpdated(_manifest);
            _typeConverter.OnManifestUpdated(_manifest);
            _wellKnownTypes.OnManifestUpdated(_manifest);
            _typeCodec.ClearCaches();
        }

        Refreshed?.Invoke();
    }

    /// <summary>
    /// Resets the lazily initialized static member accessors of the assembly's generated codecs: a hot
    /// reload can rename a member while its field-id-keyed accessor field survives, leaving a cached
    /// delegate bound to the old backing field. Release-shaped assemblies are skipped via IsInitOnly.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "Hot reload inspects generated codec fields only in metadata-update-capable, non-trimmed development processes.")]
    private void ClearGeneratedAccessorCaches(Assembly assembly)
    {
        try
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.Namespace is not { } ns || !ns.StartsWith("OrleansCodeGen", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsInitOnly)
                    {
                        continue;
                    }

                    if (field.Name.StartsWith("setField_", StringComparison.Ordinal) || field.Name.StartsWith("getField_", StringComparison.Ordinal))
                    {
                        field.SetValue(null, null);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            OnFailure($"Failed to clear generated accessor caches for assembly '{assembly.GetName().Name}'.", exception);
        }
    }

    private void OnFailure(string message, Exception exception)
    {
        System.Diagnostics.Debug.WriteLine($"{message} {exception}");
        RefreshFailed?.Invoke(message, exception);
    }

    public void Dispose() => HotReloadMetadataUpdateHandler.Unregister(this);
}
#endif
