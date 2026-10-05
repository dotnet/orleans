using System;

namespace Orleans;

/// <summary>
/// Specifies the construction delegate for generated proxies derived from this base type.
/// </summary>
/// <remarks>
/// The generator emits a static <c>Create</c> method with the delegate's signature and registers
/// it for each concrete proxy interface. The delegate must be closed and return a proxy by value.
/// Its parameters are forwarded to the generated proxy constructor.
/// Derived proxy bases inherit the declaration and can supply their own declaration to override it.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class GenerateProxyFactoryAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateProxyFactoryAttribute"/> class.
    /// </summary>
    /// <param name="delegateType">The delegate type describing proxy construction.</param>
    public GenerateProxyFactoryAttribute(Type delegateType) =>
        DelegateType = delegateType ?? throw new ArgumentNullException(nameof(delegateType));

    /// <summary>
    /// Gets the delegate type describing proxy construction.
    /// </summary>
    public Type DelegateType { get; }
}
