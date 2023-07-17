using System;
using MessagePack;
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
    private static readonly ServiceDescriptor ServiceDescriptor = new (typeof(MessagePackCodec), typeof(MessagePackCodec));

    /// <summary>
    /// Adds support for serializing and deserializing values using <see cref="MessagePackSerializer"/>.
    /// </summary>
    /// <param name="serializerBuilder">The serializer builder.</param>
    /// <param name="isSerializable">A delegate used to indicate which types should be serialized by this codec.</param>
    /// <param name="isCopyable">A delegate used to indicate which types should be copied by this codec.</param>
    /// <param name="messagePackSerializerOptions">The MessagePack serializer options.</param>
    public static ISerializerBuilder AddMessagePackSerializer(
        this ISerializerBuilder serializerBuilder,
        Func<Type, bool> isSerializable = null,
        Func<Type, bool> isCopyable = null,
        MessagePackSerializerOptions messagePackSerializerOptions = null)
    {
        return serializerBuilder.AddMessagePackSerializer(
            isSerializable,
            isCopyable,
            optionsBuilder => optionsBuilder.Configure(options =>
            {
                if (messagePackSerializerOptions is not null)
                {
                    options.SerializerOptions = messagePackSerializerOptions;
                }
            })
        );
    }

    /// <summary>
    /// Adds support for serializing and deserializing values using <see cref="MessagePackSerializer"/>.
    /// </summary>
    /// <param name="serializerBuilder">The serializer builder.</param>
    /// <param name="isSerializable">A delegate used to indicate which types should be serialized by this codec.</param>
    /// <param name="isCopyable">A delegate used to indicate which types should be copied by this codec.</param>
    /// <param name="configureOptions">A delegate used to configure the options for the MessagePack codec.</param>
    public static ISerializerBuilder AddMessagePackSerializer(
        this ISerializerBuilder serializerBuilder,
        Func<Type, bool> isSerializable,
        Func<Type, bool> isCopyable,
        Action<OptionsBuilder<MessagePackCodecOptions>> configureOptions = null)
    {
        var services = serializerBuilder.Services;
        if (configureOptions != null)
        {
            configureOptions(services.AddOptions<MessagePackCodecOptions>());
        }

        if (isSerializable != null)
        {
            services.AddSingleton<ICodecSelector>(new DelegateCodecSelector
            {
                CodecName = MessagePackCodec.WellKnownAlias,
                IsSupportedTypeDelegate = isSerializable
            });
        }

        if (isCopyable != null)
        {
            services.AddSingleton<ICopierSelector>(new DelegateCopierSelector
            {
                CopierName = MessagePackCodec.WellKnownAlias,
                IsSupportedTypeDelegate = isCopyable
            });
        }

        if (!services.Contains(ServiceDescriptor))
        {
            services.AddSingleton<MessagePackCodec>();
            services.AddFromExisting<IGeneralizedCodec, MessagePackCodec>();
            services.AddFromExisting<IGeneralizedCopier, MessagePackCodec>();
            services.AddFromExisting<ITypeFilter, MessagePackCodec>();
            serializerBuilder.Configure(options => options.WellKnownTypeAliases[MessagePackCodec.WellKnownAlias] = typeof(MessagePackCodec));
        }

        return serializerBuilder;
    }
}