using System;

namespace Orleans;

/// <summary>
/// Declares a closed serializable type and its dependency graph for a generated serializer context.
/// </summary>
/// <typeparam name="T">The closed root type to register.</typeparam>
/// <remarks>
/// Apply this attribute to a partial class derived from <c>Orleans.Serialization.SerializerContext</c>.
/// Each root must be a closed type supported by the context generator. Register the generated context
/// using <c>AddSerializerContext</c> to enable explicit type lookup.
/// Use <see cref="SerializerContextImportAttribute{TContext}"/> to include graphs provided by other
/// contexts. Included model roots use their producer's factories and require only reference metadata.
/// External models and their generated codecs and copiers are consumed through full implementation
/// metadata. This supplies the complete dependency graph, including private state and specialized
/// accessors. Other consumers can use reference assemblies from the same producer.
/// F# model reference declarations supply the required header facts when their generated C# codecs
/// and copiers are provided through full implementation metadata.
/// Generic attributes require C# 11 or later.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class OrleansSerializableAttribute<T> : Attribute;
