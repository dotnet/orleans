#if NET6_0_OR_GREATER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using Orleans.Serialization.Configuration;

[assembly: MetadataUpdateHandler(typeof(Orleans.Serialization.Hosting.HotReloadMetadataUpdateHandler))]

namespace Orleans.Serialization.Hosting;

/// <summary>
/// A container-scoped participant in the refresh performed by <see cref="HotReloadMetadataUpdateHandler"/>.
/// </summary>
internal interface IHotReloadRefreshParticipant
{
    /// <summary>
    /// Gets the refresh order: the serialization layer (phase 0) refreshes before the grain metadata
    /// layers, which consume its results.
    /// </summary>
    int Phase { get; }

    /// <summary>
    /// Refreshes state derived from the type manifests of <paramref name="updatedAssemblies"/>,
    /// or of all known assemblies when <see langword="null"/>.
    /// </summary>
    void Refresh(HashSet<Assembly>? updatedAssemblies);
}

/// <summary>
/// Notifies registered <see cref="IHotReloadRefreshParticipant"/>s when a .NET Hot Reload update touches
/// an assembly containing generated serializer metadata, making types added by the edit usable without a restart.
/// </summary>
internal static class HotReloadMetadataUpdateHandler
{
    private static readonly object SyncRoot = new();
    private static readonly List<WeakReference<IHotReloadRefreshParticipant>> Participants = new();
    private static readonly ConcurrentDictionary<Assembly, bool> ManifestAssemblyCache = new();

    public static void Register(IHotReloadRefreshParticipant participant)
    {
        lock (SyncRoot)
        {
            Participants.Add(new WeakReference<IHotReloadRefreshParticipant>(participant));
        }
    }

    public static void Unregister(IHotReloadRefreshParticipant participant)
    {
        lock (SyncRoot)
        {
            Participants.RemoveAll(weak => !weak.TryGetTarget(out var target) || ReferenceEquals(target, participant));
        }
    }

    /// <summary>
    /// Invoked by the runtime after a metadata update batch has been applied and all handlers' caches have
    /// been cleared. <paramref name="updatedTypes"/> may be <see langword="null"/> or empty when the set of
    /// changed types is unknown.
    /// </summary>
    public static void UpdateApplication(Type[]? updatedTypes)
    {
        try
        {
            HashSet<Assembly>? updatedAssemblies = null;
            if (updatedTypes is { Length: > 0 })
            {
                updatedAssemblies = new HashSet<Assembly>();
                foreach (var type in updatedTypes)
                {
                    var assembly = type.Assembly;
                    if (ManifestAssemblyCache.GetOrAdd(assembly, static a => a.IsDefined(typeof(TypeManifestProviderAttribute))))
                    {
                        updatedAssemblies.Add(assembly);
                    }
                }

                if (updatedAssemblies.Count == 0)
                {
                    return;
                }
            }

            List<IHotReloadRefreshParticipant> participants = new();
            lock (SyncRoot)
            {
                Participants.RemoveAll(static weak => !weak.TryGetTarget(out _));
                foreach (var weak in Participants)
                {
                    if (weak.TryGetTarget(out var participant))
                    {
                        participants.Add(participant);
                    }
                }
            }

            participants.Sort(static (left, right) => left.Phase.CompareTo(right.Phase));
            foreach (var participant in participants)
            {
                try
                {
                    participant.Refresh(updatedAssemblies);
                }
                catch
                {
                    // Participants log their own failures; a metadata update handler must never throw,
                    // as that would fault the hot reload apply itself.
                }
            }
        }
        catch
        {
            // See above: never throw from a metadata update handler.
        }
    }
}
#endif
