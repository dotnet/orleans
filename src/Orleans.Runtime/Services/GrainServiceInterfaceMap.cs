using System;
using System.Collections.Generic;
using Orleans.CodeGeneration;
using Orleans.Services;

namespace Orleans.Runtime;

/// <summary>
/// Chooses the system-target identity a grain service already had, and the other interface addresses that share that instance.
/// </summary>
internal static class GrainServiceInterfaceMap
{
    internal static Type GetPrimaryInterface(Type serviceType)
    {
        var candidates = GetCandidates(serviceType);
        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(string.Format($"Cannot find an interface on {serviceType.FullName} which implements IGrainService"));
        }

        return SelectPrimary(candidates);
    }

    /// <summary>
    /// Returns interfaces that should resolve to <paramref name="serviceType"/> without becoming its primary identity.
    /// An interface implemented by more than one registered service is omitted. That keeps a shared interface on the
    /// service whose primary it already is, and leaves it unregistered when it was nobody's primary.
    /// </summary>
    internal static Type[] GetAliasInterfaces(Type serviceType, IReadOnlyList<Type> registeredServiceTypes)
    {
        var candidates = GetCandidates(serviceType);
        if (candidates.Length == 0)
        {
            return [];
        }

        var primary = SelectPrimary(candidates);
        var aliases = new List<Type>();
        foreach (var candidate in candidates)
        {
            if (candidate == primary)
            {
                continue;
            }

            if (CountRegistrations(candidate, registeredServiceTypes) == 1)
            {
                aliases.Add(candidate);
            }
        }

        return aliases.ToArray();
    }

    /// <summary>
    /// Builds the grain id a client of <paramref name="interfaceType"/> uses, on the same silo as <paramref name="primaryId"/>.
    /// </summary>
    internal static GrainId CreateAliasGrainId(GrainId primaryId, Type interfaceType)
    {
        var typeCode = GrainInterfaceUtils.GetGrainClassTypeCode(interfaceType);
        var grainType = SystemTargetGrainId.CreateGrainServiceGrainType(typeCode, null);
        return new GrainId(grainType, primaryId.Key);
    }

    private static Type[] GetCandidates(Type serviceType)
        => Array.FindAll(serviceType.GetInterfaces(), static x => x.GetInterfaces().Contains(typeof(IGrainService)));

    private static Type SelectPrimary(Type[] candidates)
    {
        // GetCandidates preserves Type.GetInterfaces() order, which is the order Array.Find used.
        // That interface is the system-target id existing clients already call. Replacing it with the
        // most-derived interface drops a shared base that was this service's id, and assigns one leaf
        // id to two services that previously owned different interfaces.
        return candidates[0];
    }

    private static int CountRegistrations(Type interfaceType, IReadOnlyList<Type> registeredServiceTypes)
    {
        var count = 0;
        foreach (var serviceType in registeredServiceTypes)
        {
            if (Array.IndexOf(GetCandidates(serviceType), interfaceType) >= 0)
            {
                count++;
            }
        }

        return count;
    }
}
