using System;

namespace Orleans;

/// <summary>
/// Generates closed serializer and copier registrations for the specified roots and their serialization dependencies.
/// </summary>
/// <remarks>
/// Apply this attribute to a partial class derived from <c>Orleans.Serialization.SerializerContext</c>.
/// Each root must be a closed type supported by the context generator. Register the generated context
/// using <c>AddSerializerContext</c> to enable explicit type lookup.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class GenerateSerializerContextAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateSerializerContextAttribute"/> class.
    /// </summary>
    /// <param name="type">A closed root type to register.</param>
    public GenerateSerializerContextAttribute(Type type) => Type = type;

    /// <summary>
    /// Gets the root type whose serialization dependencies are registered.
    /// </summary>
    public Type Type { get; }
}
