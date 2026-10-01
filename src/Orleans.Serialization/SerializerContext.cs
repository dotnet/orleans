using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Configuration;
using Orleans.Serialization.Serializers;

namespace Orleans.Serialization;

/// <summary>
/// Provides compile-time registrations for a closed graph of serializable types.
/// </summary>
/// <remarks>
/// Derive a partial class from this type and apply <see cref="GenerateSerializerContextAttribute"/>.
/// Register its instance using <see cref="SerializerBuilderExtensions.AddSerializerContext"/>.
/// Multiple contexts contribute to the same serializer configuration.
/// </remarks>
public abstract class SerializerContext : TypeManifestProviderBase
{
    /// <summary>
    /// Creates a statically closed holder which resolves a recursive codec dependency during construction.
    /// </summary>
    /// <typeparam name="T">The serialized type.</typeparam>
    /// <param name="provider">The codec provider.</param>
    /// <returns>A holder for the registered codec.</returns>
    protected static IFieldCodec<T> CreateCodecHolder<T>(ICodecProvider provider)
        => new ServiceCollectionExtensions.FieldCodecHolder<T>(provider);

    /// <summary>
    /// Creates a statically closed holder which resolves a recursive copier dependency on first use.
    /// </summary>
    /// <typeparam name="T">The copied type.</typeparam>
    /// <param name="provider">The codec provider.</param>
    /// <returns>A holder for the registered copier.</returns>
    protected static IDeepCopier<T> CreateCopierHolder<T>(ICodecProvider provider)
        => new ServiceCollectionExtensions.CopierHolder<T>(provider);
}
