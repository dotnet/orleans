using System;
using System.Collections.Generic;
using Orleans.CodeGeneration;
using Orleans.Services;

namespace Orleans.Runtime;

/// <summary>
/// Chooses the single system-target identity of a grain service and the other interface addresses that share it.
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
    /// An interface implemented by more than one registered service is omitted so it is not given to two instances.
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
        // An interface that another candidate extends is a parent. The remaining interfaces are the most derived.
        var mostDerived = Array.FindAll(candidates, candidate => !Array.Exists(candidates, other => other != candidate && other.GetInterfaces().Contains(candidate)));
        var pool = mostDerived.Length > 0 ? mostDerived : candidates;
        if (pool.Length == 1)
        {
            return pool[0];
        }

        Array.Sort(pool, static (left, right) => string.Compare(left.FullName, right.FullName, StringComparison.Ordinal));
        return pool[0];
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
