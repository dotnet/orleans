using System;
using System.Collections.Generic;
#if NET5_0_OR_GREATER
using System.Diagnostics.CodeAnalysis;
#endif

namespace Orleans.Serialization.Configuration;

/// <summary>
/// Registers interface proxies and strongly typed construction delegates.
/// </summary>
/// <typeparam name="TFactory">The delegate type used to construct proxies.</typeparam>
public sealed class InterfaceProxyFactoryOptions<TFactory> where TFactory : Delegate
{
    internal Dictionary<Type, (Type ProxyType, TFactory? Factory)> Factories { get; } = new();

    // Superseded registrations remain explicit proxies, so legacy discovery cannot displace their replacements.
    internal HashSet<Type> ProxyTypes { get; } = new();

    /// <summary>
    /// Registers a factory for a concrete interface.
    /// </summary>
    /// <param name="interfaceType">The interface implemented by the proxy.</param>
    /// <param name="proxyType">The proxy type.</param>
    /// <param name="factory">The delegate which constructs a proxy.</param>
    public void Add(Type interfaceType, Type proxyType, TFactory factory)
    {
        if (interfaceType is null)
        {
            throw new ArgumentNullException(nameof(interfaceType));
        }

        if (proxyType is null)
        {
            throw new ArgumentNullException(nameof(proxyType));
        }

        if (factory is null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        ProxyTypes.Add(proxyType);
        Factories[interfaceType] = (proxyType, factory);
    }

    /// <summary>
    /// Registers an open generic proxy for runtime-selected generic arguments.
    /// </summary>
    /// <param name="interfaceType">The open generic interface.</param>
    /// <param name="proxyType">The open generic generated proxy type.</param>
    /// <remarks>
    /// Statically closed registrations added using <see cref="Add(Type, Type, TFactory)"/> take precedence.
    /// Runtime-selected generic arguments use reflective proxy construction.
    /// </remarks>
    public void Add(
        Type interfaceType,
#if NET5_0_OR_GREATER
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
#endif
        Type proxyType)
    {
        if (interfaceType is null)
        {
            throw new ArgumentNullException(nameof(interfaceType));
        }

        if (proxyType is null)
        {
            throw new ArgumentNullException(nameof(proxyType));
        }

        ProxyTypes.Add(proxyType);
        Factories[interfaceType] = (proxyType, null);
    }
}
