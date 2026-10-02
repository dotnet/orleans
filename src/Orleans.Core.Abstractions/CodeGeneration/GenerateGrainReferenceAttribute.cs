using System;

namespace Orleans;

/// <summary>
/// Generates and registers a statically constructed reference for the specified grain interface.
/// </summary>
/// <remarks>
/// Apply this attribute to an application assembly for each closed generic grain interface used
/// under NativeAOT. The generator also supports interfaces declared in referenced assemblies.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class GenerateGrainReferenceAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateGrainReferenceAttribute"/> class.
    /// </summary>
    /// <param name="interfaceType">The concrete grain interface for which to generate a factory.</param>
    public GenerateGrainReferenceAttribute(Type interfaceType) => InterfaceType = interfaceType;

    /// <summary>
    /// Gets the concrete grain interface for which to generate a factory.
    /// </summary>
    public Type InterfaceType { get; }
}
