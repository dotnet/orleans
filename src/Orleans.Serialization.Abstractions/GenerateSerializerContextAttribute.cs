using System;

namespace Orleans;

/// <summary>
/// Generates closed serializer and copier registrations for a root type and its serialization dependencies.
/// </summary>
/// <typeparam name="T">The closed root type to register.</typeparam>
/// <remarks>
/// Apply this attribute to a partial class derived from <c>Orleans.Serialization.SerializerContext</c>.
/// Each root must be a closed type supported by the context generator. Register the generated context
/// using <c>AddSerializerContext</c> to enable explicit type lookup.
/// External models and their generated codecs and copiers are consumed through full implementation
/// metadata. This supplies the complete dependency graph, including private state and specialized
/// accessors. Other consumers can use reference assemblies from the same producer.
/// F# model reference declarations supply the required header facts when their generated C# codecs
/// and copiers are provided through full implementation metadata.
/// Generic attributes require C# 11 or later.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class GenerateSerializerContextAttribute<T> : Attribute;
