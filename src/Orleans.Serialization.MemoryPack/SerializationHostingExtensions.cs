using System;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Utilities.Internal;

namespace Orleans.Serialization;

/// <summary>
/// Extension method for <see cref="ISerializerBuilder"/>.
/// </summary>
public static class SerializationHostingExtensions
{
    private static readonly ServiceDescriptor ServiceDescriptor = new(typeof(MemoryPackCodec), typeof(MemoryPackCodec));

    /// <summary>
    /// Adds support for serializing and deserializing values using <see cref="MemoryPackSerializer"/>.
    /// </summary>
    /// <param name="serializerBuilder">The serializer builder.</param>
    /// <param name="isSerializable">A delegate used to indicate which types should be serialized by this codec.</param>
    /// <param name="isCopyable">A delegate used to indicate which types should be copied by this codec.</param>
    /// <param name="memoryPackSerializerOptions">The MemoryPack serializer options.</param>
    public static ISerializerBuilder AddMemoryPackSerializer(
        this ISerializerBuilder serializerBuilder,
        Func<Type, bool> isSerializable = null,
        Func<Type, bool> isCopyable = null,
        MemoryPackSerializerOptions memoryPackSerializerOptions = null)
    {
        return serializerBuilder.AddMemoryPackSerializer(
            isSerializable,
            isCopyable,
            optionsBuilder => optionsBuilder.Configure(options =>
            {
                if (memoryPackSerializerOptions is not null)
                {
                    options.SerializerOptions = memoryPackSerializerOptions;
                }
            })
        );
    }

    /// <summary>
    /// Adds support for serializing and deserializing values using <see cref="MemoryPackSerializer"/>.
    /// </summary>
    /// <param name="serializerBuilder">The serializer builder.</param>
    /// <param name="isSerializable">A delegate used to indicate which types should be serialized by this codec.</param>
    /// <param name="isCopyable">A delegate used to indicate which types should be copied by this codec.</param>
    /// <param name="configureOptions">A delegate used to configure the options for the MemoryPack codec.</param>
    public static ISerializerBuilder AddMemoryPackSerializer(
        this ISerializerBuilder serializerBuilder,
        Func<Type, bool> isSerializable,
        Func<Type, bool> isCopyable,
        Action<OptionsBuilder<MemoryPackCodecOptions>> configureOptions = null)
    {
        var services = serializerBuilder.Services;
        if (configureOptions != null)
        {
            configureOptions(services.AddOptions<MemoryPackCodecOptions>());
        }

        if (isSerializable != null)
        {
            services.AddSingleton<ICodecSelector>(new DelegateCodecSelector
            {
                CodecName = MemoryPackCodec.WellKnownAlias,
                IsSupportedTypeDelegate = isSerializable
            });
        }

        if (isCopyable != null)
        {
            services.AddSingleton<ICopierSelector>(new DelegateCopierSelector
            {
                CopierName = MemoryPackCodec.WellKnownAlias,
                IsSupportedTypeDelegate = isCopyable
            });
        }

        if (!services.Contains(ServiceDescriptor))
        {
            services.AddSingleton<MemoryPackCodec>();
            services.AddFromExisting<IGeneralizedCodec, MemoryPackCodec>();
            services.AddFromExisting<IGeneralizedCopier, MemoryPackCodec>();
            services.AddFromExisting<ITypeFilter, MemoryPackCodec>();
            serializerBuilder.Configure(options => options.WellKnownTypeAliases[MemoryPackCodec.WellKnownAlias] = typeof(MemoryPackCodec));
        }

        return serializerBuilder;
    }
}
