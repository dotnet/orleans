using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
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
    private static readonly ServiceDescriptor ServiceDescriptor = new (typeof(NewtonsoftJsonCodec), typeof(NewtonsoftJsonCodec));

    /// <summary>
    /// Adds support for serializing and deserializing values using <see cref="JsonSerializer"/>.
    /// </summary>
    /// <param name="serializerBuilder">The serializer builder.</param>
    /// <param name="isSupported">A delegate used to indicate which types should be serialized and copied by this codec.</param>
    /// <param name="jsonSerializerSettings">The JSON serializer settings.</param>
    public static ISerializerBuilder AddNewtonsoftJsonSerializer(
        this ISerializerBuilder serializerBuilder,
        Func<Type, bool> isSupported,
        JsonSerializerSettings jsonSerializerSettings = null)
        => serializerBuilder.AddNewtonsoftJsonSerializer(
            isSupported,
            optionsBuilder => optionsBuilder.Configure(options =>
            {
                if (jsonSerializerSettings is not null)
                {
                    options.SerializerSettings = jsonSerializerSettings;
                }
            }));

    /// <summary>
    /// Adds support for serializing and deserializing values using <see cref="JsonSerializer"/>.
    /// </summary>
    /// <param name="serializerBuilder">The serializer builder.</param>
    /// <param name="isSupported">A delegate used to indicate which types should be serialized and copied by this codec.</param>
    /// <param name="configureOptions">A delegate used to configure the options for the JSON serializer.</param>
    public static ISerializerBuilder AddNewtonsoftJsonSerializer(
        this ISerializerBuilder serializerBuilder,
        Func<Type, bool> isSupported,
        Action<OptionsBuilder<NewtonsoftJsonCodecOptions>> configureOptions)
        => serializerBuilder.AddNewtonsoftJsonSerializer(
            isSupported,
            isSupported,
            configureOptions);

    /// <summary>
    /// Adds support for serializing and deserializing values using <see cref="JsonSerializer"/>.
    /// </summary>
    /// <param name="serializerBuilder">The serializer builder.</param>
    /// <param name="isSerializable">A delegate used to indicate which types should be serialized by this codec.</param>
    /// <param name="isCopyable">A delegate used to indicate which types should be copied by this codec.</param>
    /// <param name="configureOptions">A delegate used to configure the options for the JSON serializer.</param>
    public static ISerializerBuilder AddNewtonsoftJsonSerializer(
        this ISerializerBuilder serializerBuilder,
        Func<Type, bool> isSerializable,
        Func<Type, bool> isCopyable,
        Action<OptionsBuilder<NewtonsoftJsonCodecOptions>> configureOptions)
    {
        var services = serializerBuilder.Services;
        if (configureOptions != null)
        {
            configureOptions(services.AddOptions<NewtonsoftJsonCodecOptions>());
        }

        if (isSerializable != null)
        {
            services.AddSingleton<ICodecSelector>(new DelegateCodecSelector
            {
                CodecName = NewtonsoftJsonCodec.WellKnownAlias,
                IsSupportedTypeDelegate = isSerializable
            });
        }

        if (isCopyable != null)
        {
            services.AddSingleton<ICopierSelector>(new DelegateCopierSelector
            {
                CopierName = NewtonsoftJsonCodec.WellKnownAlias,
                IsSupportedTypeDelegate = isCopyable
            });
        }

        if (!services.Contains(ServiceDescriptor))
        {
            services.AddSingleton<NewtonsoftJsonCodec>();
            services.AddFromExisting<IGeneralizedCodec, NewtonsoftJsonCodec>();
            services.AddFromExisting<IGeneralizedCopier, NewtonsoftJsonCodec>();
            services.AddFromExisting<ITypeFilter, NewtonsoftJsonCodec>();
            serializerBuilder.Configure(options => options.WellKnownTypeAliases[NewtonsoftJsonCodec.WellKnownAlias] = typeof(NewtonsoftJsonCodec));
        }

        return serializerBuilder;
    }
}