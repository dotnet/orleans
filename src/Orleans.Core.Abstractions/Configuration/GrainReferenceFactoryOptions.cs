using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Orleans.Runtime;

namespace Orleans.Configuration;

/// <summary>
/// Registers generated grain-reference factories and their declared grain interfaces.
/// </summary>
public sealed class GrainReferenceFactoryOptions
{
    internal Dictionary<Type, (Type ProxyType, Func<GrainReferenceShared, IdSpan, GrainReference>? Factory)> Factories { get; } = new();

    // Superseded registrations remain explicit proxies, so legacy discovery cannot displace their replacements.
    internal HashSet<Type> ProxyTypes { get; } = new();

    /// <summary>
    /// Registers a factory for a concrete grain interface.
    /// </summary>
    /// <param name="interfaceType">The grain interface implemented by the proxy.</param>
    /// <param name="proxyType">The generated proxy type.</param>
    /// <param name="factory">The factory which constructs a reference using shared state and a grain key.</param>
    public void Add(Type interfaceType, Type proxyType, Func<GrainReferenceShared, IdSpan, GrainReference> factory)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);
        ArgumentNullException.ThrowIfNull(proxyType);
        ArgumentNullException.ThrowIfNull(factory);
        ProxyTypes.Add(proxyType);
        Factories[interfaceType] = (proxyType, factory);
    }

    /// <summary>
    /// Registers an open generic proxy for runtime-selected generic arguments.
    /// </summary>
    /// <param name="interfaceType">The open generic grain interface.</param>
    /// <param name="proxyType">The open generic generated proxy type.</param>
    /// <remarks>
    /// Statically closed registrations added using <see cref="Add(Type, Type, Func{GrainReferenceShared, IdSpan, GrainReference})"/> take precedence.
    /// Runtime-selected generic arguments use reflective proxy construction.
    /// </remarks>
    public void Add(
        Type interfaceType,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type proxyType)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);
        ArgumentNullException.ThrowIfNull(proxyType);
        ProxyTypes.Add(proxyType);
        Factories[interfaceType] = (proxyType, null);
    }
}
