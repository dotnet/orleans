using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Utilities.Internal;
using System;
using System.Text.Json;

namespace Orleans.Serialization;

/// <summary>
/// Extension method for <see cref="ISerializerBuilder"/>.
/// </summary>
public static class SerializationHostingExtensions
{
    private static readonly ServiceDescriptor ServiceDescriptor = new (typeof(JsonCodec), typeof(JsonCodec));

    /// <summary>
    /// Adds support for serializing and deserializing values using <see cref="JsonSerializer"/>.
    /// </summary>
    /// <param name="serializerBuilder">The serializer builder.</param>
    /// <param name="isSupported">A delegate used to indicate which types should be serialized and copied by this codec.</param>
    /// <param name="jsonSerializerOptions">The JSON serializer options.</param>
    public static ISerializerBuilder AddJsonSerializer(
        this ISerializerBuilder serializerBuilder,
        Func<Type, bool> isSupported,
        JsonSerializerOptions jsonSerializerOptions = null)
        => serializerBuilder.AddJsonSerializer(
            isSupported,
            isSupported,
            optionsBuilder => optionsBuilder.Configure(options =>
            {
                if (jsonSerializerOptions is not null)
                {
                    options.SerializerOptions = jsonSerializerOptions;
                }
            }));

    /// <summary>
    /// Adds support for serializing and deserializing values using <see cref="JsonSerializer"/>.
    /// </summary>
    /// <param name="serializerBuilder">The serializer builder.</param>
    /// <param name="isSerializable">A delegate used to indicate which types should be serialized by this codec.</param>
    /// <param name="isCopyable">A delegate used to indicate which types should be copied by this codec.</param>
    /// <param name="configureOptions">A delegate used to configure the options for the JSON serializer.</param>
    public static ISerializerBuilder AddJsonSerializer(
        this ISerializerBuilder serializerBuilder,
        Func<Type, bool> isSerializable,
        Func<Type, bool> isCopyable,
        Action<OptionsBuilder<JsonCodecOptions>> configureOptions = null)
    {
        var services = serializerBuilder.Services;
        if (configureOptions != null)
        {
            configureOptions(services.AddOptions<JsonCodecOptions>());
        }

        if (isSerializable != null)
        {
            services.AddSingleton<ICodecSelector>(new DelegateCodecSelector
            {
                CodecName = JsonCodec.WellKnownAlias,
                IsSupportedTypeDelegate = isSerializable
            });
        }

        if (isCopyable != null)
        {
            services.AddSingleton<ICopierSelector>(new DelegateCopierSelector
            {
                CopierName = JsonCodec.WellKnownAlias,
                IsSupportedTypeDelegate = isCopyable
            });
        }

        if (!services.Contains(ServiceDescriptor))
        {
            services.AddSingleton<JsonCodec>();
            services.AddFromExisting<IGeneralizedCodec, JsonCodec>();
            services.AddFromExisting<IGeneralizedCopier, JsonCodec>();
            services.AddFromExisting<ITypeFilter, JsonCodec>();
            serializerBuilder.Configure(options => options.WellKnownTypeAliases[JsonCodec.WellKnownAlias] = typeof(JsonCodec));
        }

        return serializerBuilder;
    }
}