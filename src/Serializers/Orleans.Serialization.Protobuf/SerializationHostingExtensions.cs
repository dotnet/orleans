using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Utilities.Internal;
using System;

namespace Orleans.Serialization;

/// <summary>
/// Extension method for <see cref="ISerializerBuilder"/>.
/// </summary>
public static class SerializationHostingExtensions
{
    private static readonly ServiceDescriptor ServiceDescriptor = new (typeof(ProtobufCodec), typeof(ProtobufCodec));

    /// <summary>
    /// Adds support for serializing and deserializing Protobuf IMessage types using <see cref="MessageParser"/>.
    /// </summary>
    /// <param name="serializerBuilder">The serializer builder.</param>
    public static ISerializerBuilder AddProtobufSerializer(
        this ISerializerBuilder serializerBuilder)
        => serializerBuilder.AddProtobufSerializer(
            isSerializable: type => typeof(IMessage).IsAssignableFrom(type),
            isCopyable: type => typeof(IMessage).IsAssignableFrom(type));

    /// <summary>
    /// Adds support for serializing and deserializing Protobuf IMessage types using <see cref="MessageParser"/>.
    /// </summary>
    /// <param name="serializerBuilder">The serializer builder.</param>
    /// <param name="isSerializable">A delegate used to indicate which types should be serialized by this codec.</param>
    /// <param name="isCopyable">A delegate used to indicate which types should be copied by this codec.</param>
    public static ISerializerBuilder AddProtobufSerializer(
        this ISerializerBuilder serializerBuilder,
        Func<Type, bool> isSerializable,
        Func<Type, bool> isCopyable)
    {
        var services = serializerBuilder.Services;

        if (isSerializable != null)
        {
            services.AddSingleton<ICodecSelector>(new DelegateCodecSelector
            {
                CodecName = ProtobufCodec.WellKnownAlias,
                IsSupportedTypeDelegate = isSerializable
            });
        }

        if (isCopyable != null)
        {
            services.AddSingleton<ICopierSelector>(new DelegateCopierSelector
            {
                CopierName = ProtobufCodec.WellKnownAlias,
                IsSupportedTypeDelegate = isCopyable
            });
        }

        if (!services.Contains(ServiceDescriptor))
        {
            services.AddSingleton<ProtobufCodec>();
            services.AddFromExisting<IGeneralizedCodec, ProtobufCodec>();
            services.AddFromExisting<IGeneralizedCopier, ProtobufCodec>();
            services.AddFromExisting<ITypeFilter, ProtobufCodec>();

            serializerBuilder.Configure(options => options.WellKnownTypeAliases[ProtobufCodec.WellKnownAlias] = typeof(ProtobufCodec));
        }

        return serializerBuilder;
    }
}