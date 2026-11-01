using System;

namespace Orleans;

/// <summary>
/// Includes the closed type graph provided by another generated serializer context.
/// </summary>
/// <typeparam name="TContext">The concrete serializer context to include.</typeparam>
/// <remarks>
/// Apply this attribute to a partial class derived from <c>Orleans.Serialization.SerializerContext</c>.
/// The included context requires accessible parameterless construction and declares its roots using
/// <see cref="GenerateSerializerContextAttribute{T}"/> or links using this attribute.
/// Generation treats those roots as provided endpoints and traverses the remaining local graph.
/// The included context supplies its own factories and dependency metadata, so consumers can use
/// reference assemblies. Includes form an acyclic graph, ensuring each root has an owning context.
/// Shared links are configured once per type-manifest provider key.
/// Register the resulting context using <c>AddSerializerContext</c>.
/// Generic attributes require C# 11 or later.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class IncludeSerializerContextAttribute<TContext> : Attribute;
